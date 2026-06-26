using Socigy.OpenSource.DB.Core.Delegates;
using Socigy.OpenSource.DB.Core.Parsers;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text;
using static Socigy.OpenSource.DB.Core.SyntaxHelper.DB;

namespace Socigy.OpenSource.DB.Core.Parsers.Postgresql
{
    public class PostgresqlWhereVisitor : ExpressionVisitor, ISqlVisitor, IParameterRecorder
    {
        private readonly StringBuilder _Sql = new();
        private readonly DbCommand _Command;
        private readonly GetColumnName _GetColumnName;
        private readonly ParameterExpression _rowParam;
        private readonly Dictionary<string, FlaggedEnumJoinInfo>? _flaggedEnums;
        private readonly ParameterFinder _finder;

        // Parameters captured during translation (binding order), used to build the cache replay plan.
        private readonly List<RecordedParameter> _recorded = new();
        IReadOnlyList<RecordedParameter> IParameterRecorder.RecordedParameters => _recorded;

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
            _Sql.Clear();
            _recorded.Clear();
            _Sql.Append(" WHERE ");
            Visit(expression);
            return _Sql.ToString();
        }

        /// <summary>
        /// Creates a parameter from a source sub-expression + transform, appends its placeholder, and
        /// records it for the replay plan. <paramref name="raw"/> is the already-evaluated source value.
        /// </summary>
        private void EmitParam(Expression source, ParamTransform transform, object? raw, Type? arrayElementType = null)
        {
            object? value = WhereParameter.Apply(transform, raw, arrayElementType);
            string paramName = $"@p{_Command.Parameters.Count}";
            var p = _Command.CreateParameter();
            p.ParameterName = paramName;
            p.Value = value ?? DBNull.Value;
            _Command.Parameters.Add(p);
            _Sql.Append(paramName);
            _recorded.Add(new RecordedParameter(source, transform, arrayElementType));
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (TryEvaluate(node, out var value)) { EmitParam(node, ParamTransform.Value, value); return node; }

            if (node.NodeType == ExpressionType.Not)
            {
                _Sql.Append(" NOT (");
                Visit(node.Operand);
                _Sql.Append(")");
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

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (TryEvaluate(node, out var value)) { EmitParam(node, ParamTransform.Value, value); return node; }

            if (IsNullConstant(node.Right)) { Visit(node.Left); _Sql.Append(node.NodeType == ExpressionType.Equal ? " IS NULL" : " IS NOT NULL"); return node; }
            if (IsNullConstant(node.Left)) { Visit(node.Right); _Sql.Append(node.NodeType == ExpressionType.Equal ? " IS NULL" : " IS NOT NULL"); return node; }

            // Null-coalescing (a ?? b) maps to COALESCE(a, b), not an infix operator.
            if (node.NodeType == ExpressionType.Coalesce)
            {
                _Sql.Append("COALESCE(");
                Visit(node.Left);
                _Sql.Append(", ");
                Visit(node.Right);
                _Sql.Append(")");
                return node;
            }

            _Sql.Append("(");
            Visit(node.Left);

            switch (node.NodeType)
            {
                case ExpressionType.AndAlso: _Sql.Append(" AND "); break;
                case ExpressionType.OrElse: _Sql.Append(" OR "); break;
                case ExpressionType.Equal: _Sql.Append(" = "); break;
                case ExpressionType.NotEqual: _Sql.Append(" <> "); break;
                case ExpressionType.GreaterThan: _Sql.Append(" > "); break;
                case ExpressionType.GreaterThanOrEqual: _Sql.Append(" >= "); break;
                case ExpressionType.LessThan: _Sql.Append(" < "); break;
                case ExpressionType.LessThanOrEqual: _Sql.Append(" <= "); break;
                case ExpressionType.Add: _Sql.Append(" + "); break;
                case ExpressionType.Subtract: _Sql.Append(" - "); break;
                case ExpressionType.Multiply: _Sql.Append(" * "); break;
                case ExpressionType.Divide: _Sql.Append(" / "); break;
                case ExpressionType.Modulo: _Sql.Append(" % "); break;
                // Fail fast instead of emitting invalid SQL like "... AndAlso ...".
                default:
                    throw new NotSupportedException(
                        $"Unsupported binary operator '{node.NodeType}' in SQL WHERE translation: {node}");
            }

            Visit(node.Right);
            _Sql.Append(")");
            return node;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == _rowParam)
            {
                _Sql.Append(_GetColumnName(node.Member.Name));
                return node;
            }

            // Nullable<T> access on a column: x.Col.HasValue / x.Col.Value
            if (node.Expression is MemberExpression inner && inner.Expression == _rowParam)
            {
                if (node.Member.Name == "HasValue")
                {
                    _Sql.Append(_GetColumnName(inner.Member.Name));
                    _Sql.Append(" IS NOT NULL");
                    return node;
                }
                if (node.Member.Name == "Value")
                {
                    _Sql.Append(_GetColumnName(inner.Member.Name));
                    return node;
                }
            }

            if (TryEvaluate(node, out var value))
            {
                EmitParam(node, ParamTransform.Value, value);
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
                    object? normalized = WhereParameter.Normalize(enumVal);

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
                    p.Value = normalized ?? DBNull.Value;
                    _Command.Parameters.Add(p);
                    _recorded.Add(new RecordedParameter(node.Arguments[0], ParamTransform.Value, null));

                    if (!first) sb.Append(" AND ");
                    sb.Append($"\"{joinInfo.JunctionTable}\".\"{joinInfo.EnumFkColumn}\" = {paramName})");

                    _Sql.Append(sb);
                    return node;
                }
            }

            // DynamicTable custom (undeclared) column reference: DB.CustomField<T>("col") => "col"
            if (node.Method.DeclaringType == typeof(global::Socigy.OpenSource.DB.Core.SyntaxHelper.DB)
                && node.Method.Name == "CustomField")
            {
                if (TryEvaluate(node.Arguments[0], out var __colName) && __colName is string __col)
                {
                    _Sql.Append('"').Append(__col.Replace("\"", "\"\"")).Append('"');
                    return node;
                }
            }

            if (IsSqlMarker(node)) return VisitSqlMarkers(node);

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
                    _Sql.Append(" = ANY(");
                    var elementType = itemExpr is UnaryExpression conv
                        && (conv.NodeType == ExpressionType.Convert || conv.NodeType == ExpressionType.ConvertChecked)
                        ? conv.Operand.Type
                        : itemExpr.Type;
                    EmitParam(collectionExpr, ParamTransform.TypedArray, collection, elementType);
                    _Sql.Append(")");
                    return node;
                }
            }

            // Partial evaluation
            if (TryEvaluate(node, out var value))
            {
                EmitParam(node, ParamTransform.Value, value);
                return node;
            }

            throw new NotSupportedException(
                $"Unsupported method call '{node.Method.DeclaringType?.Name}.{node.Method.Name}' in SQL WHERE translation: {node}");
        }

        private bool IsSqlMarker(MethodCallExpression node)
        {
            var type = node.Method.DeclaringType;
            return type == typeof(Select) || type == typeof(Query);
        }

        private Expression VisitSqlMarkers(MethodCallExpression node)
        {
            if (node.Method.Name == "Custom")
            {
                if (TryEvaluate(node.Arguments[0], out var sql)) _Sql.Append(sql?.ToString() ?? "");
                return node;
            }
            ParseFluentCase(node);
            return node;
        }

        private void ParseFluentCase(MethodCallExpression node)
        {
            if (node.Object is MethodCallExpression parent) ParseFluentCase(parent);
            else if (node.Method.Name == "Case") { _Sql.Append("CASE"); return; }

            switch (node.Method.Name)
            {
                case "When": _Sql.Append(" WHEN "); Visit(node.Arguments[0]); break;
                case "Then": _Sql.Append(" THEN "); Visit(node.Arguments[0]); break;
                case "Else": _Sql.Append(" ELSE "); Visit(node.Arguments[0]); _Sql.Append(" END"); break;
            }
        }

        private Expression HandleStringMethods(MethodCallExpression node)
        {
            string name = node.Method.Name;

            // Static string.IsNullOrEmpty(x.Col) / string.IsNullOrWhiteSpace(x.Col)
            if (node.Object == null && (name == "IsNullOrEmpty" || name == "IsNullOrWhiteSpace") && node.Arguments.Count == 1)
            {
                _Sql.Append("(");
                Visit(node.Arguments[0]);
                _Sql.Append(" IS NULL OR ");
                Visit(node.Arguments[0]);
                _Sql.Append(name == "IsNullOrWhiteSpace" ? " ~ '^\\s*$'" : " = ''");
                _Sql.Append(")");
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
                _Sql.Append(likeOp);
                var transform = name == "Contains" ? ParamTransform.LikeContains
                    : name == "StartsWith" ? ParamTransform.LikeStartsWith
                    : ParamTransform.LikeEndsWith;
                EmitParam(node.Arguments[0], transform, Evaluate(node.Arguments[0]));
                _Sql.Append(" ESCAPE '\\'");
                return node;
            }

            if (name == "Equals")
            {
                if (node.Object != null) { Visit(node.Object); _Sql.Append(" = "); EmitParam(node.Arguments[0], ParamTransform.Value, Evaluate(node.Arguments[0])); }
                else if (node.Arguments.Count >= 2) { Visit(node.Arguments[0]); _Sql.Append(" = "); EmitParam(node.Arguments[1], ParamTransform.Value, Evaluate(node.Arguments[1])); }
                else throw new NotSupportedException($"Unsupported string.Equals overload in SQL WHERE translation: {node}");
                return node;
            }

            // Bare ToLower()/ToUpper() used directly in a comparison: LOWER("col") / UPPER("col").
            if (name == "ToLower" || name == "ToUpper")
            {
                _Sql.Append(name == "ToLower" ? "LOWER(" : "UPPER(");
                Visit(node.Object);
                _Sql.Append(")");
                return node;
            }

            throw new NotSupportedException($"Unsupported string method '{name}' in SQL WHERE translation: {node}");
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            EmitParam(node, ParamTransform.Value, node.Value);
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
            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                // DB.CustomField("col") is a column reference, not a constant — never fold it away.
                if (node.Method.DeclaringType == typeof(global::Socigy.OpenSource.DB.Core.SyntaxHelper.DB)
                    && node.Method.Name == "CustomField")
                    IsFound = true;
                return base.VisitMethodCall(node);
            }
        }
    }
}
