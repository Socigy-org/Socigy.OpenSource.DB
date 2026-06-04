using Socigy.OpenSource.DB.Core.Delegates;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text;
using static Socigy.OpenSource.DB.Core.SyntaxHelper.DB;

namespace Socigy.OpenSource.DB.Core.Parsers.Postgresql
{
    public class PostgresqlWhereVisitor : ExpressionVisitor, ISqlVisitor
    {
        private readonly StringBuilder _Sql = new();
        private readonly DbCommand _Command;
        private readonly GetColumnName _GetColumnName;
        private readonly ParameterExpression _rowParam;
        private readonly Dictionary<string, FlaggedEnumJoinInfo>? _flaggedEnums;
        private readonly ParameterFinder _finder;

        // When false (BindParameters / cache-hit mode) the traversal still appends parameters to the
        // command but emits no SQL — the SQL is already known from the cache.
        private bool _emit = true;

        private void Emit(string s) { if (_emit) _Sql.Append(s); }
        private void Emit(StringBuilder s) { if (_emit) _Sql.Append(s); }

        /// <summary>Creates a visitor without flagged-enum join support.</summary>
        public PostgresqlWhereVisitor(ParameterExpression rowParam, GetColumnName getColumnName, DbCommand command)
            : this(rowParam, getColumnName, command, null) { }

        /// <summary>
        /// Creates a visitor with optional flagged-enum join info so that
        /// <c>x.Property.HasFlag(value)</c> expressions translate to
        /// <c>EXISTS (SELECT 1 FROM junction WHERE fk = main.pk AND enum_fk = @v)</c>.
        /// </summary>
        public PostgresqlWhereVisitor(ParameterExpression rowParam, GetColumnName getColumnName, DbCommand command,
            Dictionary<string, FlaggedEnumJoinInfo>? flaggedEnums)
        {
            _rowParam = rowParam;
            _GetColumnName = getColumnName;
            _Command = command;
            _flaggedEnums = flaggedEnums;
            _finder = new ParameterFinder(rowParam);
        }

        public string Parse(Expression expression)
        {
            _emit = true;
            _Sql.Clear();
            Emit(" WHERE ");
            Visit(expression);
            return _Sql.ToString();
        }

        /// <summary>
        /// Re-binds the predicate's parameters to the command without emitting SQL — the same traversal
        /// as <see cref="Parse"/>, so parameters are added in identical order to match cached SQL.
        /// </summary>
        public void BindParameters(Expression expression)
        {
            _emit = false;
            try { Visit(expression); }
            finally { _emit = true; }
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

        private void AddParameter(object? value)
        {
            value = NormalizeParameterValue(value);
            string paramName = $"@p{_Command.Parameters.Count}";
            var p = _Command.CreateParameter();
            p.ParameterName = paramName;
            p.Value = value ?? DBNull.Value;
            _Command.Parameters.Add(p);
            Emit(paramName);
        }

        // ---------------------------------------------------------
        // 1. Unary Expressions
        // ---------------------------------------------------------
        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (TryEvaluate(node, out var value)) { AddParameter(value); return node; }

            if (node.NodeType == ExpressionType.Not)
            {
                Emit(" NOT (");
                Visit(node.Operand);
                Emit(")");
                return node;
            }

            if (node.NodeType == ExpressionType.Convert || node.NodeType == ExpressionType.ConvertChecked)
            {
                Visit(node.Operand);
                return node;
            }

            throw new NotSupportedException(
                $"Unsupported unary operator '{node.NodeType}' in SQL WHERE translation: {node}");
        }

        // -------------------------------------------------------------------------
        // 2. Binary Expressions
        // -------------------------------------------------------------------------
        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (TryEvaluate(node, out var value)) { AddParameter(value); return node; }

            if (IsNullConstant(node.Right)) { Visit(node.Left); Emit(node.NodeType == ExpressionType.Equal ? " IS NULL" : " IS NOT NULL"); return node; }
            if (IsNullConstant(node.Left)) { Visit(node.Right); Emit(node.NodeType == ExpressionType.Equal ? " IS NULL" : " IS NOT NULL"); return node; }

            // Null-coalescing (a ?? b) maps to COALESCE(a, b), not an infix operator.
            if (node.NodeType == ExpressionType.Coalesce)
            {
                Emit("COALESCE(");
                Visit(node.Left);
                Emit(", ");
                Visit(node.Right);
                Emit(")");
                return node;
            }

            Emit("(");
            Visit(node.Left);

            switch (node.NodeType)
            {
                case ExpressionType.AndAlso: Emit(" AND "); break;
                case ExpressionType.OrElse: Emit(" OR "); break;
                case ExpressionType.Equal: Emit(" = "); break;
                case ExpressionType.NotEqual: Emit(" <> "); break;
                case ExpressionType.GreaterThan: Emit(" > "); break;
                case ExpressionType.GreaterThanOrEqual: Emit(" >= "); break;
                case ExpressionType.LessThan: Emit(" < "); break;
                case ExpressionType.LessThanOrEqual: Emit(" <= "); break;
                case ExpressionType.Add: Emit(" + "); break;
                case ExpressionType.Subtract: Emit(" - "); break;
                case ExpressionType.Multiply: Emit(" * "); break;
                case ExpressionType.Divide: Emit(" / "); break;
                case ExpressionType.Modulo: Emit(" % "); break;
                // Fail fast instead of emitting invalid SQL like "... AndAlso ...".
                default:
                    throw new NotSupportedException(
                        $"Unsupported binary operator '{node.NodeType}' in SQL WHERE translation: {node}");
            }

            Visit(node.Right);
            Emit(")");
            return node;
        }

        // -------------------------------------------------------------------------
        // 3. Member Access & Method Calls
        // -------------------------------------------------------------------------
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == _rowParam)
            {
                Emit(_GetColumnName(node.Member.Name));
                return node;
            }

            // Nullable<T> access on a column: x.Col.HasValue / x.Col.Value
            if (node.Expression is MemberExpression inner && inner.Expression == _rowParam)
            {
                if (node.Member.Name == "HasValue")
                {
                    Emit(_GetColumnName(inner.Member.Name));
                    Emit(" IS NOT NULL");
                    return node;
                }
                if (node.Member.Name == "Value")
                {
                    Emit(_GetColumnName(inner.Member.Name));
                    return node;
                }
            }

            if (TryEvaluate(node, out var value))
            {
                AddParameter(value);
                return node;
            }

            throw new NotSupportedException(
                $"Unsupported member access '{node.Member.Name}' in SQL WHERE translation: {node}. " +
                "Nested navigation properties are not supported — use a join or a flat column.");
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // HasFlag on a flagged-enum property: x.Role.HasFlag(UserRole.Admin)
            if (node.Method.Name == "HasFlag"
                && node.Object is MemberExpression memberExpr
                && memberExpr.Expression == _rowParam
                && _flaggedEnums != null
                && _flaggedEnums.TryGetValue(memberExpr.Member.Name, out var joinInfo))
            {
                if (TryEvaluate(node.Arguments[0], out var enumVal))
                {
                    enumVal = NormalizeParameterValue(enumVal);

                    var sb = new StringBuilder();
                    sb.Append($"EXISTS (SELECT 1 FROM \"{joinInfo.JunctionTable}\" WHERE ");

                    bool first = true;
                    foreach (var (mainPk, junctionFk) in joinInfo.PkMappings)
                    {
                        if (!first) sb.Append(" AND ");
                        sb.Append($"\"{joinInfo.JunctionTable}\".\"{junctionFk}\" = \"{joinInfo.MainTable}\".\"{mainPk}\"");
                        first = false;
                    }

                    string paramName = $"@p{_Command.Parameters.Count}";
                    var p = _Command.CreateParameter();
                    p.ParameterName = paramName;
                    p.Value = enumVal ?? DBNull.Value;
                    _Command.Parameters.Add(p);

                    if (!first) sb.Append(" AND ");
                    sb.Append($"\"{joinInfo.JunctionTable}\".\"{joinInfo.EnumFkColumn}\" = {paramName})");

                    Emit(sb);
                    return node;
                }
            }

            // SQL Markers
            if (IsSqlMarker(node)) return VisitSqlMarkers(node);

            // String methods
            if (IsDependentOnParam(node) && node.Method.DeclaringType == typeof(string))
                return HandleStringMethods(node);

            // IEnumerable.Contains(x.Property) => column = ANY(@pN)
            if (node.Method.Name == "Contains" && node.Method.DeclaringType != typeof(string))
            {
                Expression? collectionExpr = null;
                Expression? itemExpr = null;

                if (node.Object != null && node.Arguments.Count == 1)
                {
                    collectionExpr = node.Object;
                    itemExpr = node.Arguments[0];
                }
                else if (node.Object == null && node.Arguments.Count == 2)
                {
                    collectionExpr = node.Arguments[0];
                    itemExpr = node.Arguments[1];
                }

                if (collectionExpr != null && itemExpr != null
                    && IsDependentOnParam(itemExpr) && !IsDependentOnParam(collectionExpr)
                    && TryEvaluate(collectionExpr, out var collection))
                {
                    Visit(itemExpr);
                    Emit(" = ANY(");
                    var elementType = itemExpr is UnaryExpression conv
                        && (conv.NodeType == ExpressionType.Convert || conv.NodeType == ExpressionType.ConvertChecked)
                        ? conv.Operand.Type
                        : itemExpr.Type;
                    AddParameter(ExpressionEvaluator.ToTypedArray(collection, elementType));
                    Emit(")");
                    return node;
                }
            }

            // Partial evaluation
            if (TryEvaluate(node, out var value))
            {
                AddParameter(value);
                return node;
            }

            throw new NotSupportedException(
                $"Unsupported method call '{node.Method.DeclaringType?.Name}.{node.Method.Name}' in SQL WHERE translation: {node}");
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------
        private bool IsSqlMarker(MethodCallExpression node)
        {
            var type = node.Method.DeclaringType;
            return type == typeof(Select) || type == typeof(Query);
        }

        private Expression VisitSqlMarkers(MethodCallExpression node)
        {
            if (node.Method.Name == "Custom")
            {
                if (TryEvaluate(node.Arguments[0], out var sql)) Emit(sql?.ToString() ?? "");
                return node;
            }
            ParseFluentCase(node);
            return node;
        }

        private void ParseFluentCase(MethodCallExpression node)
        {
            if (node.Object is MethodCallExpression parent) ParseFluentCase(parent);
            else if (node.Method.Name == "Case") { Emit("CASE"); return; }

            switch (node.Method.Name)
            {
                case "When": Emit(" WHEN "); Visit(node.Arguments[0]); break;
                case "Then": Emit(" THEN "); Visit(node.Arguments[0]); break;
                case "Else": Emit(" ELSE "); Visit(node.Arguments[0]); Emit(" END"); break;
            }
        }

        private Expression HandleStringMethods(MethodCallExpression node)
        {
            string name = node.Method.Name;

            // Static string.IsNullOrEmpty(x.Col) / string.IsNullOrWhiteSpace(x.Col)
            if (node.Object == null && (name == "IsNullOrEmpty" || name == "IsNullOrWhiteSpace") && node.Arguments.Count == 1)
            {
                Emit("(");
                Visit(node.Arguments[0]);
                Emit(" IS NULL OR ");
                Visit(node.Arguments[0]);
                Emit(name == "IsNullOrWhiteSpace" ? " ~ '^\\s*$'" : " = ''");
                Emit(")");
                return node;
            }

            // Detect a case-insensitive target: x.Col.ToLower()/ToUpper() wrapping a LIKE-family call.
            bool caseInsensitive = false;
            Expression? target = node.Object;
            if (target is MethodCallExpression mc && mc.Object != null &&
                (mc.Method.Name == "ToLower" || mc.Method.Name == "ToUpper"))
            {
                caseInsensitive = true;
                target = mc.Object;
            }

            string likeOp = caseInsensitive ? " ILIKE " : " LIKE ";

            if (name == "Contains" || name == "StartsWith" || name == "EndsWith")
            {
                Visit(target);
                var raw = Evaluate(node.Arguments[0])?.ToString() ?? "";
                var esc = EscapeLike(raw);
                string pattern = name == "Contains" ? $"%{esc}%" : name == "StartsWith" ? $"{esc}%" : $"%{esc}";
                Emit(likeOp);
                AddParameter(pattern);
                Emit(" ESCAPE '\\'");
                return node;
            }

            if (name == "Equals")
            {
                if (node.Object != null) { Visit(node.Object); Emit(" = "); AddParameter(Evaluate(node.Arguments[0])); }
                else if (node.Arguments.Count >= 2) { Visit(node.Arguments[0]); Emit(" = "); AddParameter(Evaluate(node.Arguments[1])); }
                else throw new NotSupportedException($"Unsupported string.Equals overload in SQL WHERE translation: {node}");
                return node;
            }

            // Bare ToLower()/ToUpper() used directly in a comparison: LOWER("col") / UPPER("col").
            if (name == "ToLower" || name == "ToUpper")
            {
                Emit(name == "ToLower" ? "LOWER(" : "UPPER(");
                Visit(node.Object);
                Emit(")");
                return node;
            }

            throw new NotSupportedException($"Unsupported string method '{name}' in SQL WHERE translation: {node}");
        }

        /// <summary>Escapes LIKE wildcards so user values match literally (used with <c>ESCAPE '\'</c>).</summary>
        private static string EscapeLike(string value) =>
            value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        protected override Expression VisitConstant(ConstantExpression node)
        {
            AddParameter(node.Value);
            return node;
        }

        private bool IsNullConstant(Expression exp) => exp is ConstantExpression c && c.Value == null;

        private static object? Evaluate(Expression e) => ExpressionEvaluator.Evaluate(e);

        private bool TryEvaluate(Expression e, out object? result)
        {
            if (IsDependentOnParam(e)) { result = null; return false; }
            try { result = Evaluate(e); return true; }
            catch { result = null; return false; }
        }

        private bool IsDependentOnParam(Expression e)
        {
            // Reused (not re-allocated) per node — the check is synchronous and non-reentrant.
            _finder.Reset();
            _finder.Visit(e);
            return _finder.IsFound;
        }

        class ParameterFinder : ExpressionVisitor
        {
            private readonly ParameterExpression _param;
            public bool IsFound { get; private set; }
            public ParameterFinder(ParameterExpression param) => _param = param;
            public void Reset() => IsFound = false;
            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (node == _param) IsFound = true;
                return node;
            }
        }
    }
}
