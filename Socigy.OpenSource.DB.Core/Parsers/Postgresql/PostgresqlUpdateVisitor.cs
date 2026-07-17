using Socigy.OpenSource.DB.Core.Delegates;
using Socigy.OpenSource.DB.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Socigy.OpenSource.DB.Core.Parsers.Postgresql
{
#nullable enable
    public class PostgresqlUpdateVisitor : ExpressionVisitor, ISqlVisitor
    {
        private class ParameterFinder : ExpressionVisitor
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

        private readonly StringBuilder _Sql = new();
        private readonly ParameterExpression _rowParam;
        private readonly DbCommand _Command;
        private readonly GetColumnName _GetColumnName;
        private readonly object? _Entity;

        private bool _extractionMode;
        private HashSet<string>? _extractedNames;

        private bool _firstAssignment = true;
        private readonly HashSet<string> _emittedMembers = new();

        public PostgresqlUpdateVisitor(
            ParameterExpression rowParam,
            GetColumnName getColumnName,
            DbCommand command,
            object? entity = null)
        {
            _rowParam = rowParam;
            _GetColumnName = getColumnName;
            _Command = command;
            _Entity = entity;
        }

        /// <summary>
        /// Generates a SET clause fragment, e.g. <c>"email" = @p0, "username" = @p1</c>.
        /// Requires a non-null entity passed to the constructor.
        /// </summary>
        public string Parse(Expression expression)
        {
            _Sql.Clear();
            _firstAssignment = true;
            _extractionMode = false;
            Visit(expression);
            return _Sql.ToString();
        }

        /// <summary>
        /// AOT-safe SET builder: emits the SET assignments for the named columns (C# property names) directly,
        /// without an <c>Expression</c> selector (which forces <c>Expression.NewArrayInit</c>, <c>[RequiresDynamicCode]</c>).
        /// Reuses <see cref="EmitAssignment"/> so quoting, value reading (incl. value convertors), JSON casting and
        /// normalization match the expression path. Requires a non-null entity.
        /// </summary>
        public string ParseColumns(string[] memberNames)
        {
            _Sql.Clear();
            _firstAssignment = true;
            _extractionMode = false;
            foreach (var name in memberNames)
                if (!string.IsNullOrEmpty(name))
                    EmitAssignment(name);
            return _Sql.ToString();
        }

        /// <summary>
        /// Walks the expression and returns the C# property names of every column
        /// referenced on the row parameter — without touching SQL or parameters.
        /// Used by <c>ExceptFields</c> to build an exclusion set.
        /// </summary>
        public HashSet<string> ExtractColumnNames(Expression expression)
        {
            _extractedNames = new HashSet<string>(StringComparer.Ordinal);
            _extractionMode = true;
            Visit(expression);
            _extractionMode = false;
            return _extractedNames;
        }

        // x => new object?[] { x.Email, x.Username, ... }
        protected override Expression VisitNewArray(NewArrayExpression node)
        {
            foreach (var expr in node.Expressions)
                Visit(expr);
            return node;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == _rowParam)
            {
                if (_extractionMode)
                    _extractedNames!.Add(node.Member.Name);
                else
                    EmitAssignment(node.Member.Name);

                return node;
            }

            return base.VisitMember(node);
        }

        // x => x.IsEmailVerified ? x.Boom : x.Shoom
        // Condition is evaluated against the entity at SQL-build time so we always
        // emit a single concrete column (CASE is illegal as a SET left-hand side).
        protected override Expression VisitConditional(ConditionalExpression node)
        {
            bool branch = EvaluateBoolean(node.Test);
            Visit(branch ? node.IfTrue : node.IfFalse);
            return node;
        }

        private static object? NormalizeDbValue(object? value)
        {
            if (value == null || value == DBNull.Value)
                return value;

            // Match the insert/full-update/COPY/WHERE normalizations so a selective (WithFields) update binds the
            // same value the full-field path would: a Kind=Utc DateTime relabeled Unspecified (a naive 'timestamp'
            // column, else Npgsql infers 'timestamptz' and PostgreSQL shifts it by the session TimeZone); a
            // non-zero-offset DateTimeOffset normalized to UTC (Npgsql rejects a non-UTC offset for 'timestamptz');
            // unsigned types widened (no Npgsql wire mapping); and an enum bound as its underlying integer.
            if (value is DateTime dt && dt.Kind == DateTimeKind.Utc)
                return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            if (value is DateTimeOffset dto && dto.Offset != TimeSpan.Zero)
                return dto.ToUniversalTime();
            if (value is ushort us) return (int)us;
            if (value is uint ui) return (long)ui;
            if (value is ulong ul) return (decimal)ul;
            if (value is Enum enumValue)
                return Convert.ChangeType(enumValue, Enum.GetUnderlyingType(enumValue.GetType()));

            return value;
        }

        private void EmitAssignment(string memberName)
        {
            // Skip a member already assigned in this selector (e.g. WithFields(x => new[]{ x.Email, x.Email })),
            // since PostgreSQL rejects "col = .., col = .." (multiple assignments to the same column).
            if (!_emittedMembers.Add(memberName)) return;
            if (!_firstAssignment) _Sql.Append(", ");
            _firstAssignment = false;

            string column = _GetColumnName(memberName)
                ?? throw new NotSupportedException($"No database column is mapped for member '{memberName}'.");
            var info = ReadColumnInfo(memberName);
            object? value = NormalizeDbValue(info.HasValue ? info.Value.Value : ReadEntityValueReflection(memberName));
            string paramName = $"@p{_Command.Parameters.Count}";

            var p = _Command.CreateParameter();
            p.ParameterName = paramName;
            p.Value = value ?? DBNull.Value;
            _Command.Parameters.Add(p);

            // A jsonb column receives a serialized JSON *string*; cast it so PostgreSQL doesn't reject
            // 'jsonb = text'. (Encrypted columns bind as byte[] which Npgsql already infers as bytea.)
            _Sql.Append(info.HasValue && info.Value.IsJson ? $"{column} = {paramName}::jsonb" : $"{column} = {paramName}");
        }

        // When the entity implements IDbTable, GetColumn returns the ColumnInfo so any ValueConvertor is
        // applied (via ColumnInfo.Value) and column traits (IsJson/IsEncrypted) are available.
        private global::Socigy.OpenSource.DB.Core.CommandBuilders.ColumnInfo? ReadColumnInfo(string memberName)
        {
            if (_Entity is IDbTable dbTable)
            {
                var dbColName = dbTable.GetDbColumnName(memberName);
                if (dbColName != null)
                {
                    var col = dbTable.GetColumn(dbColName);
                    if (col.HasValue)
                        return col.Value.Info;
                }
            }
            return null;
        }

        private object? ReadEntityValueReflection(string memberName)
        {
            if (_Entity is null) return null;
            var type = _Entity.GetType();
            return type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(_Entity)
                ?? type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(_Entity);
        }

        private bool EvaluateBoolean(Expression test)
        {
            // Interpret the test against the concrete entity via reflection (no Expression.Compile, which is
            // [RequiresDynamicCode] and unusable under NativeAOT). The row parameter is bound to the entity; a
            // param-independent test ignores it.
            return ExpressionEvaluator.EvaluateWithParameter(test, _rowParam, _Entity) is true;
        }

        private bool IsDependentOnParam(Expression e)
        {
            var finder = new ParameterFinder(_rowParam);
            finder.Visit(e);
            return finder.IsFound;
        }
    }
#nullable disable

}
