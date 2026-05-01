using Socigy.OpenSource.DB.Core.Delegates;
using System;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text;

namespace Socigy.OpenSource.DB.Core.Parsers.Postgresql
{
#nullable enable
    /// <summary>
    /// Translates a two-parameter expression <c>Expression&lt;Func&lt;T, TJoin, bool&gt;&gt;</c>
    /// into a parameterised SQL condition fragment (ON or WHERE clause body).
    /// Member accesses on the first parameter are qualified with <paramref name="leftAlias"/>;
    /// accesses on the second are qualified with <paramref name="rightAlias"/>.
    /// </summary>
    public class PostgresqlJoinConditionVisitor : ExpressionVisitor
    {
        private readonly StringBuilder _Sql = new();
        private readonly DbCommand _Command;
        private readonly ParameterExpression _leftParam;
        private readonly ParameterExpression _rightParam;
        private readonly GetColumnName _getLeftColumn;
        private readonly GetColumnName _getRightColumn;
        private readonly string _leftAlias;
        private readonly string _rightAlias;

        public PostgresqlJoinConditionVisitor(
            ParameterExpression leftParam,
            ParameterExpression rightParam,
            GetColumnName getLeftColumn,
            GetColumnName getRightColumn,
            DbCommand command,
            string leftAlias = "t",
            string rightAlias = "j")
        {
            _leftParam = leftParam;
            _rightParam = rightParam;
            _getLeftColumn = getLeftColumn;
            _getRightColumn = getRightColumn;
            _Command = command;
            _leftAlias = leftAlias;
            _rightAlias = rightAlias;
        }

        /// <summary>Visits <paramref name="expression"/> and returns the SQL condition fragment (no leading keyword).</summary>
        public string Parse(Expression expression)
        {
            _Sql.Clear();
            Visit(expression);
            return _Sql.ToString();
        }

        // ── Visitors ──────────────────────────────────────────────────────────────

        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (TryEvaluate(node, out var v)) { AddParameter(v); return node; }

            if (node.NodeType == ExpressionType.Not)
            {
                _Sql.Append("NOT (");
                Visit(node.Operand);
                _Sql.Append(")");
                return node;
            }
            if (node.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            {
                Visit(node.Operand);
                return node;
            }
            return base.VisitUnary(node);
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (TryEvaluate(node, out var v)) { AddParameter(v); return node; }

            if (IsNullConstant(node.Right)) { Visit(node.Left); _Sql.Append(node.NodeType == ExpressionType.Equal ? " IS NULL" : " IS NOT NULL"); return node; }
            if (IsNullConstant(node.Left)) { Visit(node.Right); _Sql.Append(node.NodeType == ExpressionType.Equal ? " IS NULL" : " IS NOT NULL"); return node; }

            _Sql.Append("(");
            Visit(node.Left);

            _Sql.Append(node.NodeType switch
            {
                ExpressionType.AndAlso => " AND ",
                ExpressionType.OrElse => " OR ",
                ExpressionType.Equal => " = ",
                ExpressionType.NotEqual => " <> ",
                ExpressionType.GreaterThan => " > ",
                ExpressionType.GreaterThanOrEqual => " >= ",
                ExpressionType.LessThan => " < ",
                ExpressionType.LessThanOrEqual => " <= ",
                _ => $" {node.NodeType} "
            });

            Visit(node.Right);
            _Sql.Append(")");
            return node;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == _leftParam)
            {
                _Sql.Append($"{_leftAlias}.{_getLeftColumn(node.Member.Name)}");
                return node;
            }
            if (node.Expression == _rightParam)
            {
                _Sql.Append($"{_rightAlias}.{_getRightColumn(node.Member.Name)}");
                return node;
            }
            if (TryEvaluate(node, out var v)) { AddParameter(v); return node; }
            return base.VisitMember(node);
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            AddParameter(node.Value);
            return node;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // Try to evaluate any method call that doesn't touch either parameter
            if (TryEvaluate(node, out var v)) { AddParameter(v); return node; }
            return base.VisitMethodCall(node);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private void AddParameter(object? value)
        {
            if (value is Enum e)
                value = Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()));

            string paramName = $"@p{_Command.Parameters.Count}";
            var p = _Command.CreateParameter();
            p.ParameterName = paramName;
            p.Value = value ?? DBNull.Value;
            _Command.Parameters.Add(p);
            _Sql.Append(paramName);
        }

        private bool IsNullConstant(Expression exp) =>
            exp is ConstantExpression c && c.Value == null;

        private object? Evaluate(Expression e)
        {
            if (e is ConstantExpression c) return c.Value;
            return Expression.Lambda(e).Compile().DynamicInvoke();
        }

        private bool TryEvaluate(Expression e, out object? result)
        {
            if (IsDependentOnParam(e)) { result = null; return false; }
            try { result = Evaluate(e); return true; }
            catch { result = null; return false; }
        }

        private bool IsDependentOnParam(Expression e)
        {
            var finder = new TwoParamFinder(_leftParam, _rightParam);
            finder.Visit(e);
            return finder.IsFound;
        }

        private sealed class TwoParamFinder : ExpressionVisitor
        {
            private readonly ParameterExpression _p1, _p2;
            public bool IsFound { get; private set; }
            public TwoParamFinder(ParameterExpression p1, ParameterExpression p2) { _p1 = p1; _p2 = p2; }
            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (node == _p1 || node == _p2) IsFound = true;
                return node;
            }
        }
    }
#nullable disable
}
