using Socigy.OpenSource.DB.Core.Delegates;
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

        // ── Visitors ───────────────────────────────────────────────────────────────

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

            if (value is Enum enumValue)
            {
                var underlyingType = Enum.GetUnderlyingType(enumValue.GetType());
                return Convert.ChangeType(enumValue, underlyingType);
            }

            return value;
        }

        private void EmitAssignment(string memberName)
        {
            if (!_firstAssignment) _Sql.Append(", ");
            _firstAssignment = false;

            string column = _GetColumnName(memberName);
            object? value = NormalizeDbValue(ReadEntityValue(memberName));
            string paramName = $"@p{_Command.Parameters.Count}";

            var p = _Command.CreateParameter();
            p.ParameterName = paramName;
            p.Value = value ?? DBNull.Value;
            _Command.Parameters.Add(p);

            _Sql.Append($"{column} = {paramName}");
        }

        private object? ReadEntityValue(string memberName)
        {
            if (_Entity is null) return null;
            var type = _Entity.GetType();
            return type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(_Entity)
                ?? type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(_Entity);
        }

        private bool EvaluateBoolean(Expression test)
        {
            if (IsDependentOnParam(test))
            {
                var lambda = Expression.Lambda(test, _rowParam);
                return lambda.Compile().DynamicInvoke(_Entity) is true;
            }
            return Expression.Lambda(test).Compile().DynamicInvoke() is true;
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
