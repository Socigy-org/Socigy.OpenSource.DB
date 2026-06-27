using Socigy.OpenSource.DB.Core.Delegates;
using Socigy.OpenSource.DB.Core.Parsers;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text;

namespace Socigy.OpenSource.DB.Core.Parsers.Postgresql
{
#nullable enable
    /// <summary>
    /// Translates an N-parameter expression (one lambda parameter per joined table) into a parameterised SQL
    /// fragment. Member access on a parameter becomes <c>alias."column"</c>; everything that doesn't touch a
    /// parameter is evaluated and bound as a command parameter. Used for ON / WHERE conditions, ORDER BY
    /// column lists, and aggregate column selectors across a multi-table join.
    /// </summary>
    public sealed class PostgresqlMultiJoinVisitor : ExpressionVisitor
    {
        private readonly StringBuilder _Sql = new();
        private readonly DbCommand _Command;
        private readonly (ParameterExpression Param, string Alias, GetColumnName GetCol)[] _params;

        public PostgresqlMultiJoinVisitor(
            IReadOnlyList<(ParameterExpression Param, string Alias, GetColumnName GetCol)> paramMap,
            DbCommand command)
        {
            _params = new (ParameterExpression, string, GetColumnName)[paramMap.Count];
            for (int i = 0; i < paramMap.Count; i++) _params[i] = paramMap[i];
            _Command = command;
        }

        /// <summary>Visits a boolean condition (ON / WHERE) and returns its SQL fragment (no leading keyword).</summary>
        public string Parse(Expression expression)
        {
            _Sql.Clear();
            Visit(expression);
            return _Sql.ToString();
        }

        /// <summary>Resolves an <c>object?[]</c> selector lambda body into a comma-separated <c>alias."col"</c> list (ORDER BY).</summary>
        public string ResolveColumnList(LambdaExpression selector, bool descending = false)
        {
            var body = selector.Body;
            IReadOnlyList<Expression> elements = body is NewArrayExpression na
                ? (IReadOnlyList<Expression>)na.Expressions
                : new[] { body };

            // Apply DESC per column: "ORDER BY a, b DESC" only sorts b descending, so a multi-key
            // descending sort must repeat the keyword on every key.
            var parts = new List<string>(elements.Count);
            foreach (var e in elements) parts.Add(descending ? ResolveColumn(e) + " DESC" : ResolveColumn(e));
            return string.Join(", ", parts);
        }

        /// <summary>Resolves a single-column selector lambda body into <c>alias."col"</c> (aggregate column).</summary>
        public string ResolveSingleColumn(LambdaExpression selector) => ResolveColumn(selector.Body);

        private string ResolveColumn(Expression e)
        {
            if (e is UnaryExpression u && (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
                e = u.Operand;

            if (e is MemberExpression m)
            {
                foreach (var p in _params)
                    if (m.Expression == p.Param)
                        return p.Alias + ".\"" + p.GetCol(m.Member.Name) + "\"";
            }
            throw new NotSupportedException($"Unsupported column expression in JOIN ORDER BY / aggregate: {e}");
        }

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

            // char comparison: C# promotes `a.Initial == 'A'` (char) to int==int — one side is Convert(char->int)
            // over the column, the other a folded int code point — which binds against character(1) as an integer
            // ("character = integer"). Bind the value back as a 1-char string. Mirrors the single-table WHERE visitor.
            if (IsComparisonOperator(node.NodeType))
            {
                bool leftChar = IsCharPromotion(node.Left);
                bool rightChar = IsCharPromotion(node.Right);
                if (leftChar ^ rightChar)
                {
                    _Sql.Append("(");
                    EmitCharComparisonOperand(node.Left, leftChar);
                    _Sql.Append(ComparisonOperatorSql(node.NodeType));
                    EmitCharComparisonOperand(node.Right, rightChar);
                    _Sql.Append(")");
                    return node;
                }
            }

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
                // string concatenation is `||` in PostgreSQL; `string + string` compiles to NodeType=Add but
                // `text + text` has no operator.
                ExpressionType.Add => node.Type == typeof(string) ? " || " : " + ",
                ExpressionType.Subtract => " - ",
                ExpressionType.Multiply => " * ",
                ExpressionType.Divide => " / ",
                ExpressionType.Modulo => " % ",
                _ => throw new NotSupportedException(
                    $"Unsupported binary operator '{node.NodeType}' in SQL JOIN translation: {node}")
            });
            Visit(node.Right);
            _Sql.Append(")");
            return node;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            foreach (var p in _params)
                if (node.Expression == p.Param)
                {
                    _Sql.Append(p.Alias).Append(".\"").Append(p.GetCol(node.Member.Name)).Append('"');
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
            if (TryEvaluate(node, out var v)) { AddParameter(v); return node; }
            return base.VisitMethodCall(node);
        }

        // An inline constructor on the value side of an ON/WHERE comparison (e.g. `a.Created > new DateTime(...)`,
        // `b.Gid == new Guid("...")`) must fold to a SINGLE parameter. Without this, the base visitor recurses
        // into the constructor arguments and emits one @p per arg ("@p0@p1@p2", broken SQL) or binds a single-arg
        // ctor's string instead of the Guid. Mirrors the single-table WHERE visitor.
        protected override Expression VisitNew(NewExpression node)
        {
            if (TryEvaluate(node, out var v)) { AddParameter(v); return node; }
            throw new NotSupportedException(
                $"Unsupported constructor '{node.Type.Name}' in SQL JOIN translation: {node}");
        }

        // A ternary in an ON/WHERE (e.g. `(a.X > 0 ? a.Y : a.Z) == b.W`) must become a SQL CASE — without this the
        // base visitor emits the branches with no CASE/WHEN/THEN scaffolding (malformed SQL). Mirrors the WHERE
        // visitor. A param-independent test is evaluated now and the chosen branch inlined.
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
                Visit(ExpressionEvaluator.Evaluate(node.Test) is true ? node.IfTrue : node.IfFalse);
            }
            return node;
        }

        private static bool IsCharPromotion(Expression e) =>
            e is UnaryExpression u && (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked)
                && u.Operand.Type == typeof(char);

        private static bool IsComparisonOperator(ExpressionType t) =>
            t is ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan
              or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

        private static string ComparisonOperatorSql(ExpressionType t) => t switch
        {
            ExpressionType.Equal => " = ",
            ExpressionType.NotEqual => " <> ",
            ExpressionType.GreaterThan => " > ",
            ExpressionType.GreaterThanOrEqual => " >= ",
            ExpressionType.LessThan => " < ",
            ExpressionType.LessThanOrEqual => " <= ",
            _ => " = "
        };

        private void EmitCharComparisonOperand(Expression side, bool isCharColumn)
        {
            if (isCharColumn) { Visit(((UnaryExpression)side).Operand); return; }
            if (TryEvaluate(side, out var v) && v != null)
                AddParameter(((char)System.Convert.ToInt32(v)).ToString());
            else
                Visit(side);
        }

        private void AddParameter(object? value)
        {
            // Use the same normalization as the single-table WHERE path (enum->underlying, DateTime Kind=Utc->
            // Unspecified, DateTimeOffset->UTC, unsigned widening). The join path previously normalized ONLY enums,
            // so a UTC DateTime, an offset DateTimeOffset, or an unsigned value bound in an ON/WHERE was silently
            // shifted by the session time zone, rejected, or unmappable — while the identical single-table query
            // was correct.
            value = WhereParameter.Normalize(value);

            string paramName = $"@p{_Command.Parameters.Count}";
            var p = _Command.CreateParameter();
            p.ParameterName = paramName;
            p.Value = value ?? DBNull.Value;
            _Command.Parameters.Add(p);
            _Sql.Append(paramName);
        }

        private static bool IsNullConstant(Expression exp) => exp is ConstantExpression c && c.Value == null;

        private bool TryEvaluate(Expression e, out object? result)
        {
            if (IsDependentOnParam(e)) { result = null; return false; }
            try { result = ExpressionEvaluator.Evaluate(e); return true; }
            catch { result = null; return false; }
        }

        private bool IsDependentOnParam(Expression e)
        {
            var finder = new ParamFinder(_params);
            finder.Visit(e);
            return finder.IsFound;
        }

        private sealed class ParamFinder : ExpressionVisitor
        {
            private readonly (ParameterExpression Param, string Alias, GetColumnName GetCol)[] _params;
            public bool IsFound { get; private set; }
            public ParamFinder((ParameterExpression, string, GetColumnName)[] ps) => _params = ps;
            protected override Expression VisitParameter(ParameterExpression node)
            {
                foreach (var p in _params) if (node == p.Param) { IsFound = true; break; }
                return node;
            }
        }
    }
#nullable disable
}
