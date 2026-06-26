using Socigy.OpenSource.DB.Core.Delegates;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text;
using static Socigy.OpenSource.DB.Core.SyntaxHelper.DB;

namespace Socigy.OpenSource.DB.Core.Parsers.Postgresql
{
    public class PostgresqlOrderByVisitor : ExpressionVisitor, ISqlVisitor
    {
        private readonly StringBuilder _Sql = new();
        private readonly DbCommand _Command;
        private readonly GetColumnName _GetColumnName;
        private readonly ParameterExpression _rowParam;

        // The default direction of the parent query (.OrderBy vs .OrderByDesc)
        private readonly bool _defaultIsDescending;

        public PostgresqlOrderByVisitor(ParameterExpression rowParam, GetColumnName getColumnName, DbCommand command, bool defaultIsDescending)
        {
            _rowParam = rowParam;
            _GetColumnName = getColumnName;
            _Command = command;
            _defaultIsDescending = defaultIsDescending;
        }

        public string Parse(Expression expression)
        {
            _Sql.Clear();
            _Sql.Append(" ORDER BY ");
            Visit(expression);
            return _Sql.ToString();
        }

        private void AddParameter(object? value)
        {
            string paramName = $"@p{_Command.Parameters.Count}";
            var p = _Command.CreateParameter();
            p.ParameterName = paramName;
            p.Value = value ?? DBNull.Value;
            _Command.Parameters.Add(p);
            _Sql.Append(paramName);
        }

        protected override Expression VisitNewArray(NewArrayExpression node)
        {
            for (int i = 0; i < node.Expressions.Count; i++)
            {
                if (i > 0) _Sql.Append(", ");

                // We treat each item in the array as a distinct root for sorting
                ProcessSortItem(node.Expressions[i], _defaultIsDescending);
            }
            return node;
        }

        // Helper to handle the logic of "Expression + Direction"
        private void ProcessSortItem(Expression exp, bool isDescendingContext)
        {
            exp = StripConversion(exp);

            if (exp is MethodCallExpression methodCall)
            {
                if (methodCall.Method.DeclaringType == typeof(OrderBy) && methodCall.Method.Name == nameof(OrderBy.Asc))
                {
                    Visit(methodCall.Arguments[0]);
                    // ASC is usually default, but we can be explicit if needed or just leave it empty
                    // _Sql.Append(" ASC"); 
                    return;
                }

                if (methodCall.Method.DeclaringType == typeof(OrderBy) && methodCall.Method.Name == nameof(OrderBy.Desc))
                {
                    Visit(methodCall.Arguments[0]);
                    _Sql.Append(" DESC");
                    return;
                }

                // We delegate back to standard Visit, which handles the Case logic
                if (methodCall.Method.DeclaringType == typeof(Select) || methodCall.Method.ReturnType == typeof(Select))
                {
                    Visit(methodCall);
                    // If the entire CASE block needs to follow the parent direction:
                    if (isDescendingContext) _Sql.Append(" DESC");
                    return;
                }
            }

            Visit(exp);

            if (isDescendingContext)
            {
                _Sql.Append(" DESC");
            }
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == _rowParam)
            {
                _Sql.Append(_GetColumnName(node.Member.Name));
                return node;
            }

            if (TryEvaluate(node, out var value))
            {
                AddParameter(value);
                return node;
            }
            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(Select) || node.Method.ReturnType == typeof(Select))
            {
                // Unwind the Fluent API stack (Else -> Then -> When -> Case)
                // We can't just rely on base.Visit because we need specific control over formatting
                ParseFluentCase(node);
                return node;
            }

            return base.VisitMethodCall(node);
        }

        // Recursively unwinds the Case fluent chain
        private void ParseFluentCase(MethodCallExpression node)
        {
            if (node.Object is MethodCallExpression parent)
            {
                ParseFluentCase(parent);
            }
            else if (node.Method.Name == "Case")
            {
                _Sql.Append("CASE");
                return; // Start of chain
            }

            switch (node.Method.Name)
            {
                case "When":
                    _Sql.Append(" WHEN ");
                    Visit(node.Arguments[0]);
                    break;
                case "Then":
                    _Sql.Append(" THEN ");
                    // Inside THEN, we treat the value as a Sort Item (it might have OrderBy.Desc)
                    ProcessSortItem(node.Arguments[0], false);
                    break;
                case "Else":
                    _Sql.Append(" ELSE ");
                    ProcessSortItem(node.Arguments[0], false);
                    _Sql.Append(" END");
                    break;
            }
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            _Sql.Append("(");
            Visit(node.Left);
            switch (node.NodeType)
            {
                case ExpressionType.Equal: _Sql.Append(" = "); break;
                case ExpressionType.AndAlso: _Sql.Append(" AND "); break;
                case ExpressionType.OrElse: _Sql.Append(" OR "); break;
                case ExpressionType.NotEqual: _Sql.Append(" <> "); break;
                case ExpressionType.GreaterThan: _Sql.Append(" > "); break;
                case ExpressionType.GreaterThanOrEqual: _Sql.Append(" >= "); break;
                case ExpressionType.LessThan: _Sql.Append(" < "); break;
                case ExpressionType.LessThanOrEqual: _Sql.Append(" <= "); break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported binary operator '{node.NodeType}' in SQL ORDER BY translation: {node}");
            }
            Visit(node.Right);
            _Sql.Append(")");
            return node;
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            AddParameter(node.Value);
            return node;
        }

        private static Expression StripConversion(Expression node)
        {
            while (node.NodeType == ExpressionType.Convert || node.NodeType == ExpressionType.ConvertChecked)
            {
                node = ((UnaryExpression)node).Operand;
            }
            return node;
        }

        private bool TryEvaluate(Expression e, out object? result)
        {
            try
            {
                if (!IsDependentOnParam(e))
                {
                    result = ExpressionEvaluator.Evaluate(e);
                    return true;
                }
            }
            catch { }
            result = null;
            return false;
        }

        private bool IsDependentOnParam(Expression e)
        {
            var finder = new ParameterFinder(_rowParam);
            finder.Visit(e);
            return finder.IsFound;
        }

        class ParameterFinder : ExpressionVisitor
        {
            private readonly ParameterExpression _param;
            public bool IsFound { get; private set; }
            public ParameterFinder(ParameterExpression param) => _param = param;
            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (node == _param) IsFound = true;
                return node;
            }
        }
    }

}
