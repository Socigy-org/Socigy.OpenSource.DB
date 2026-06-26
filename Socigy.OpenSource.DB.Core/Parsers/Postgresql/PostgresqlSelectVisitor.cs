using Socigy.OpenSource.DB.Core.Delegates;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text;
using static Socigy.OpenSource.DB.Core.SyntaxHelper.DB;

namespace Socigy.OpenSource.DB.Core.Parsers.Postgresql
{
#nullable enable
    public class PostgresqlSelectVisitor : ExpressionVisitor, ISqlVisitor
    {
        private StringBuilder _Sql = new();
        private readonly ParameterExpression _rowParam;
        private readonly DbCommand _Command = null!;
        private readonly GetColumnName _GetColumnName;

        public PostgresqlSelectVisitor(ParameterExpression rowParam, GetColumnName getColumNames, DbCommand command)
        {
            _rowParam = rowParam;
            _GetColumnName = getColumNames;
            _Command = command;
        }

        public string Parse(Expression expression)
        {
            _Sql.Clear();
            Visit(expression);
            return _Sql.ToString();
        }

        protected override Expression VisitNewArray(NewArrayExpression node)
        {
            for (int i = 0; i < node.Expressions.Count; i++)
            {
                if (i > 0) _Sql.Append(", ");
                Visit(node.Expressions[i]);
            }
            return node;
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

        protected override Expression VisitConditional(ConditionalExpression node)
        {
            if (IsDependentOnParam(node.Test))
            {
                _Sql.Append("CASE WHEN ");
                Visit(node.Test);
                _Sql.Append(" THEN ");
                Visit(node.IfTrue);
                _Sql.Append(" ELSE ");
                Visit(node.IfFalse);
                _Sql.Append(" END");
            }
            else
            {
                // Evaluate it NOW to decide which branch to take
                var result = Evaluate(node.Test);
                if (result is true)
                {
                    Visit(node.IfTrue);
                }
                else
                {
                    Visit(node.IfFalse);
                }
            }
            return node;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(string))
            {
                return HandleStringMethods(node);
            }

            if (node.Method.Name == nameof(Select.Custom))
            {
                if (TryEvaluate(node.Arguments[0], out var customSql))
                {
                    _Sql.Append(customSql);
                }
                return node;
            }
            else if (node.Method.Name == nameof(Select.All))
            {
                _Sql.Append('*');
                return node;
            }

            // Note: The tree is nested inside-out. As(Else(Then(When(Case...))))
            // We visit the Object (the parent in the chain) first to print SQL in order.

            if (node.Method.DeclaringType == typeof(Select) || node.Method.ReturnType == typeof(Select))
            {
                // Recursively go deeper to start writing from the beginning (Select.Case)
                if (node.Object != null) Visit(node.Object);
                else if (node.Method.Name == "Case") _Sql.Append("CASE");

                switch (node.Method.Name)
                {
                    case "When":
                        _Sql.Append(" WHEN ");
                        Visit(node.Arguments[0]);
                        break;
                    case "Then":
                        _Sql.Append(" THEN ");
                        Visit(node.Arguments[0]);
                        break;
                    case "Else":
                        _Sql.Append(" ELSE ");
                        Visit(node.Arguments[0]);
                        _Sql.Append(" END"); // Close the CASE block here usually
                        break;
                    case "As":
                        // "As" is likely called on the result of Else, or checking a fluent terminator
                        if (TryEvaluate(node.Arguments[0], out var alias))
                            _Sql.Append($" AS \"{alias}\"");
                        break;
                }
                return node;
            }

            return base.VisitMethodCall(node);
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
                case ExpressionType.NotEqual: _Sql.Append(" != "); break;
                case ExpressionType.GreaterThan: _Sql.Append(" > "); break;
                case ExpressionType.GreaterThanOrEqual: _Sql.Append(" >= "); break;
                case ExpressionType.LessThan: _Sql.Append(" < "); break;
                case ExpressionType.LessThanOrEqual: _Sql.Append(" <= "); break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported binary operator '{node.NodeType}' in SQL SELECT translation: {node}");
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

        private Expression HandleStringMethods(MethodCallExpression node)
        {
            Visit(node.Object);

            var rawValue = Evaluate(node.Arguments[0])?.ToString() ?? "";

            // Pre-format the string for LIKE and Parameterize it
            // This is cleaner than concatenating SQL string with ||
            if (node.Method.Name == "Contains")
            {
                _Sql.Append(" LIKE ");
                AddParameter($"%{rawValue}%");
            }
            else if (node.Method.Name == "StartsWith")
            {
                _Sql.Append(" LIKE ");
                AddParameter($"{rawValue}%");
            }
            else if (node.Method.Name == "EndsWith")
            {
                _Sql.Append(" LIKE ");
                AddParameter($"%{rawValue}");
            }

            return node;
        }

        private static object? NormalizeParameterValue(object? value)
        {
            if (value is Enum e)
            {
                var underlying = Enum.GetUnderlyingType(e.GetType());
                return Convert.ChangeType(e, underlying);
            }

            return value;
        }

        // Adds value to Command.Parameters and appends @pX to SQL
        private void AddParameter(object? value)
        {
            value = NormalizeParameterValue(value);

            string paramName = $"@p{_Command.Parameters.Count}";
            var p = _Command.CreateParameter();
            p.ParameterName = paramName;
            p.Value = value ?? DBNull.Value;
            _Command.Parameters.Add(p);

            _Sql.Append(paramName);
        }

        // Reflection-light evaluation of parameter-independent closures (constants / captured vars).
        private object? Evaluate(Expression e) => ExpressionEvaluator.Evaluate(e);

        private bool TryEvaluate(Expression e, out object? result)
        {
            try
            {
                if (!IsDependentOnParam(e))
                {
                    result = Evaluate(e);
                    return true;
                }
            }
            catch { }
            result = null;
            return false;
        }

        // Checks if the expression tree refers to our row parameter 'x'
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
#nullable disable
}
