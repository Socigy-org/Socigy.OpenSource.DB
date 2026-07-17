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

        // Maps a property name to a converter that turns the CLR comparison value into the DB-stored value (the
        // [ValueConvertor].ConvertToDbValue), or returns null for a non-convertor column. Null for tables with no
        // convertor columns (the common case) — the convertor branch in VisitBinary is then skipped entirely, so
        // there is zero per-comparison overhead. Set by the parser after construction.
        internal Func<string, Func<object?, object?>?>? ColumnConvertor { get; set; }
        private bool _usedConvertor;
        bool IParameterRecorder.UsedConvertor => _usedConvertor;

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
            _usedConvertor = false;
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

        // A ternary in a predicate (e.g. `(x.A > 0 ? x.B : x.C) == 5`) must become a SQL CASE — without this
        // override the base visitor emitted the branches with no CASE/WHEN/THEN scaffolding (malformed SQL).
        // Mirrors the SELECT/UPDATE visitors. A param-independent test is evaluated now and the branch inlined.
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
                Visit(Evaluate(node.Test) is true ? node.IfTrue : node.IfFalse);
            }
            return node;
        }

        // True if <paramref name="e"/> is a (possibly nullable-lifted) member access on the row parameter whose
        // column carries a [ValueConvertor]; <paramref name="convertor"/> is then its ConvertToDbValue wrapper.
        private bool TryGetConvertorColumn(Expression e, out Func<object?, object?>? convertor)
        {
            convertor = null;
            if (e is UnaryExpression u && (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
                e = u.Operand;
            if (e is MemberExpression m && m.Expression == _rowParam)
                convertor = ColumnConvertor!(m.Member.Name);
            return convertor != null;
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

        // One side of a char comparison: the char column (strip the Convert(char->int) and emit the member), or
        // the value side (an int code point) rebound as a 1-char string PostgreSQL compares against character(1).
        private void EmitCharComparisonOperand(Expression side, bool isCharColumn)
        {
            if (isCharColumn) { Visit(((UnaryExpression)side).Operand); return; }
            // Record the raw int value with the CharString transform (NOT the already-converted string) so the
            // cache-replay path reproduces the char rebind via WhereParameter.Apply instead of binding the raw int.
            if (TryEvaluate(side, out var v) && v != null)
                EmitParam(side, ParamTransform.CharString, v);
            else
                Visit(side);
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (TryEvaluate(node, out var value)) { EmitParam(node, ParamTransform.Value, value); return node; }

            // Only an (in)equality against null becomes IS NULL / IS NOT NULL. Other operators that can carry a
            // null literal (notably `x.Col ?? null`) must fall through to their own handling — rewriting a
            // Coalesce to "col IS NOT NULL" would emit a boolean where a value belongs.
            if (node.NodeType == ExpressionType.Equal || node.NodeType == ExpressionType.NotEqual)
            {
                if (IsNullConstant(node.Right)) { Visit(node.Left); _Sql.Append(node.NodeType == ExpressionType.Equal ? " IS NULL" : " IS NOT NULL"); return node; }
                if (IsNullConstant(node.Left)) { Visit(node.Right); _Sql.Append(node.NodeType == ExpressionType.Equal ? " IS NULL" : " IS NOT NULL"); return node; }
            }

            // Same null semantics when an operand is a captured variable/expression that evaluates to null,
            // not just a literal `null`. Otherwise `col = @p` / `col <> @p` with a NULL parameter match no
            // rows (SQL three-valued logic), silently dropping the rows the author's `== null` / `!= null`
            // expected.
            if (node.NodeType == ExpressionType.Equal || node.NodeType == ExpressionType.NotEqual)
            {
                if (TryEvaluate(node.Right, out var __rv) && __rv == null) { Visit(node.Left); _Sql.Append(node.NodeType == ExpressionType.Equal ? " IS NULL" : " IS NOT NULL"); return node; }
                if (TryEvaluate(node.Left, out var __lv) && __lv == null) { Visit(node.Right); _Sql.Append(node.NodeType == ExpressionType.Equal ? " IS NULL" : " IS NOT NULL"); return node; }
            }

            // A comparison against a [ValueConvertor] column must bind the CONVERTED value: the column stores the
            // converted form (ConvertToDbValue), so binding the raw CLR value silently matched no rows. Detect a
            // convertor-column side with an evaluable (non-null) value on the other side and run it through the
            // convertor before binding. This shape is uncacheable (the replay plan would re-bind the raw value),
            // flagged via _usedConvertor so the cache skips it. ColumnConvertor is null for tables with no convertor
            // columns, so this whole block is a single null-check for them.
            if (ColumnConvertor != null && IsComparisonOperator(node.NodeType))
            {
                Func<object?, object?>? conv = null;
                Expression? colSide = null, valSide = null;
                if (TryGetConvertorColumn(node.Left, out var __lc)) { conv = __lc; colSide = node.Left; valSide = node.Right; }
                else if (TryGetConvertorColumn(node.Right, out var __rc)) { conv = __rc; colSide = node.Right; valSide = node.Left; }

                if (conv != null && valSide != null && TryEvaluate(valSide, out var __rawVal) && __rawVal != null)
                {
                    _usedConvertor = true;
                    object? __converted = conv(__rawVal);
                    if (node.NodeType == ExpressionType.NotEqual && Nullable.GetUnderlyingType(colSide!.Type) != null)
                    {
                        // mirror the nullable-!= OR-IS-NULL semantics with the converted value
                        _Sql.Append("(");
                        Visit(colSide);
                        _Sql.Append(" <> ");
                        EmitParam(valSide, ParamTransform.Value, __converted);
                        _Sql.Append(" OR ");
                        Visit(colSide);
                        _Sql.Append(" IS NULL)");
                    }
                    else
                    {
                        _Sql.Append("(");
                        Visit(colSide!);
                        _Sql.Append(ComparisonOperatorSql(node.NodeType));
                        EmitParam(valSide, ParamTransform.Value, __converted);
                        _Sql.Append(")");
                    }
                    return node;
                }
            }

            // `col != value` over a NULLABLE column must match C# semantics: in C# `null != value` is true, but
            // SQL `col <> @p` excludes NULL rows (NULL <> v is NULL, not true), silently dropping them. Add an
            // explicit `OR col IS NULL` so the NULL rows are included, like EF Core. Scoped to nullable VALUE-TYPE
            // columns, where the CLR type precisely marks nullability (a non-nullable `int` column is unaffected);
            // the literal/captured-null operands are already handled above.
            if (node.NodeType == ExpressionType.NotEqual)
            {
                Expression? colSide = null, valSide = null;
                if (IsDependentOnParam(node.Left) && !IsDependentOnParam(node.Right)) { colSide = node.Left; valSide = node.Right; }
                else if (IsDependentOnParam(node.Right) && !IsDependentOnParam(node.Left)) { colSide = node.Right; valSide = node.Left; }

                if (colSide != null && Nullable.GetUnderlyingType(colSide.Type) != null)
                {
                    _Sql.Append("(");
                    Visit(colSide);
                    _Sql.Append(" <> ");
                    Visit(valSide!);
                    _Sql.Append(" OR ");
                    Visit(colSide);
                    _Sql.Append(" IS NULL)");
                    return node;
                }
            }

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

            // char comparison: C# promotes `x.Initial == 'A'` (char) to int==int — one side becomes a
            // Convert(char->int) over the column, the other a folded int constant (the code point). Binding the
            // int against the character(1) column produces `character(1) = integer` (operator does not exist), so
            // bind the value back as a 1-char string that PostgreSQL compares against the char column.
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
                // C# compiles `string + string` to a BinaryExpression with NodeType=Add (Method=string.Concat).
                // PostgreSQL has no `text + text` operator — string concatenation is `||`.
                case ExpressionType.Add: _Sql.Append(node.Type == typeof(string) ? " || " : " + "); break;
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
                _Sql.Append('"').Append(_GetColumnName(node.Member.Name)).Append('"');
                return node;
            }

            // Nullable<T> access on a column: x.Col.HasValue / x.Col.Value
            if (node.Expression is MemberExpression inner && inner.Expression == _rowParam)
            {
                if (node.Member.Name == "HasValue")
                {
                    _Sql.Append('"').Append(_GetColumnName(inner.Member.Name)).Append('"');
                    _Sql.Append(" IS NOT NULL");
                    return node;
                }
                if (node.Member.Name == "Value")
                {
                    _Sql.Append('"').Append(_GetColumnName(inner.Member.Name)).Append('"');
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

                    // The junction stores one row per single flag bit, so a single enum_fk comparison can only
                    // match a single flag. A composite value (A|B) or the zero value would bind the OR'd integer
                    // and silently match no rows. Require a single flag and tell the caller to combine with &&.
                    long __bits = Convert.ToInt64(normalized);
                    if (__bits == 0 || (__bits & (__bits - 1)) != 0)
                        throw new NotSupportedException(
                            $"HasFlag in a WHERE predicate supports a single flag value. Combine multiple flags with '&&', " +
                            $"e.g. x.{memberExpr.Member.Name}.HasFlag(A) && x.{memberExpr.Member.Name}.HasFlag(B).");

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
                    // `array.Contains(x)` binds to MemoryExtensions.Contains(ReadOnlySpan<T>, T) via an implicit
                    // T[] -> ReadOnlySpan<T> conversion; the span (a ref struct) can't be evaluated, so unwrap to
                    // the underlying array so the IN-list still works. The conversion appears either as a Convert
                    // node or (for the user-defined span operator) as a call to op_Implicit/op_Explicit.
                    if (collectionExpr is UnaryExpression __spanConv
                        && (__spanConv.NodeType == ExpressionType.Convert || __spanConv.NodeType == ExpressionType.ConvertChecked))
                        collectionExpr = __spanConv.Operand;
                    else if (collectionExpr is MethodCallExpression __opConv && __opConv.Method.IsSpecialName
                        && (__opConv.Method.Name == "op_Implicit" || __opConv.Method.Name == "op_Explicit")
                        && __opConv.Arguments.Count == 1)
                        collectionExpr = __opConv.Arguments[0];
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

            // A trailing StringComparison.*IgnoreCase argument also makes the match case-insensitive (ILIKE),
            // mirroring the Equals path — otherwise StartsWith/EndsWith/Contains(s, OrdinalIgnoreCase) silently
            // stayed case-sensitive.
            foreach (var __a in node.Arguments)
                if (__a.Type == typeof(StringComparison) && TryEvaluate(__a, out var __sc) && __sc is StringComparison __scv
                    && (__scv == StringComparison.OrdinalIgnoreCase
                     || __scv == StringComparison.CurrentCultureIgnoreCase
                     || __scv == StringComparison.InvariantCultureIgnoreCase))
                    caseInsensitive = true;

            string likeOp = caseInsensitive ? " ILIKE " : " LIKE ";

            if (name == "Contains" || name == "StartsWith" || name == "EndsWith")
            {
                // A null pattern would become LIKE '%%' (or '%'/' %') and match every row — almost never intended.
                // Fail fast so the caller fixes the predicate (e.g. guards the value or uses an IS NULL check).
                var likeArg = Evaluate(node.Arguments[0]);
                if (likeArg == null)
                    throw new ArgumentNullException("value",
                        $"A null argument to string.{name} cannot be translated to SQL — it would match every row. " +
                        "Pass a non-null value or use an explicit IS NULL predicate.");
                Visit(target);
                _Sql.Append(likeOp);
                var transform = name == "Contains" ? ParamTransform.LikeContains
                    : name == "StartsWith" ? ParamTransform.LikeStartsWith
                    : ParamTransform.LikeEndsWith;
                EmitParam(node.Arguments[0], transform, likeArg);
                _Sql.Append(" ESCAPE '\\'");
                return node;
            }

            if (name == "Equals")
            {
                // A trailing StringComparison must not be silently dropped to a case-sensitive match.
                bool ignoreCase = false;
                foreach (var a in node.Arguments)
                    if (a.Type == typeof(StringComparison) && TryEvaluate(a, out var sc) && sc is StringComparison scv)
                        ignoreCase = scv == StringComparison.OrdinalIgnoreCase
                                  || scv == StringComparison.CurrentCultureIgnoreCase
                                  || scv == StringComparison.InvariantCultureIgnoreCase;

                Expression left, right;
                if (node.Object != null) { left = node.Object; right = node.Arguments[0]; }
                else if (node.Arguments.Count >= 2) { left = node.Arguments[0]; right = node.Arguments[1]; }
                else throw new NotSupportedException($"Unsupported string.Equals overload in SQL WHERE translation: {node}");

                // .Equals(null) would emit "col = NULL", which is always NULL (never true) — fail fast and point
                // the caller at the correct null check, matching the captured-null ==/!= handling.
                var equalsArg = Evaluate(right);
                if (equalsArg == null)
                    throw new ArgumentNullException("value",
                        "A null argument to string.Equals cannot be translated to SQL — use \"== null\" or an IS NULL predicate instead.");
                if (ignoreCase) { _Sql.Append("LOWER("); Visit(left); _Sql.Append(") = LOWER("); EmitParam(right, ParamTransform.Value, equalsArg); _Sql.Append(")"); }
                else { Visit(left); _Sql.Append(" = "); EmitParam(right, ParamTransform.Value, equalsArg); }
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

        // An inline constructor on the value side of a comparison (e.g. `x.D > new DateOnly(2020,1,1)`,
        // `x.Gid == new Guid("...")`) must fold to a SINGLE normalized parameter. Without this, the base
        // visitor recurses into the constructor arguments, emitting one @p per arg — "@p0@p1@p2" (broken SQL)
        // for multi-arg ctors, or a wrongly-typed single arg (binding the string, not the Guid). A column-side
        // `new T(...)` is impossible in a predicate, so a param-dependent NewExpression is unsupported.
        protected override Expression VisitNew(NewExpression node)
        {
            if (TryEvaluate(node, out var value)) { EmitParam(node, ParamTransform.Value, value); return node; }
            throw new NotSupportedException(
                $"Unsupported constructor '{node.Type.Name}' in SQL WHERE translation: {node}");
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
