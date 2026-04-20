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
        }

        public string Parse(Expression expression)
        {
            _Sql.Clear();
            _Sql.Append(" WHERE ");
            Visit(expression);
            return _Sql.ToString();
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
            _Sql.Append(paramName);
        }

        // ---------------------------------------------------------
        // 1. Unary Expressions
        // ---------------------------------------------------------
        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (TryEvaluate(node, out var value)) { AddParameter(value); return node; }

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

            return base.VisitUnary(node);
        }

        // -------------------------------------------------------------------------
        // 2. Binary Expressions
        // -------------------------------------------------------------------------
        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (TryEvaluate(node, out var value)) { AddParameter(value); return node; }

            if (IsNullConstant(node.Right)) { Visit(node.Left); _Sql.Append(node.NodeType == ExpressionType.Equal ? " IS NULL" : " IS NOT NULL"); return node; }
            if (IsNullConstant(node.Left)) { Visit(node.Right); _Sql.Append(node.NodeType == ExpressionType.Equal ? " IS NULL" : " IS NOT NULL"); return node; }

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
                default: _Sql.Append($" {node.NodeType} "); break;
            }

            Visit(node.Right);
            _Sql.Append(")");
            return node;
        }

        // -------------------------------------------------------------------------
        // 3. Member Access & Method Calls
        // -------------------------------------------------------------------------
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

                    _Sql.Append(sb);
                    return node;
                }
            }

            // SQL Markers
            if (IsSqlMarker(node)) return VisitSqlMarkers(node);

            // String methods
            if (IsDependentOnParam(node) && node.Method.DeclaringType == typeof(string))
                return HandleStringMethods(node);

            // Partial evaluation
            if (TryEvaluate(node, out var value))
            {
                AddParameter(value);
                return node;
            }

            return base.VisitMethodCall(node);
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
                if (TryEvaluate(node.Arguments[0], out var sql)) _Sql.Append(sql);
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
            Visit(node.Object);
            var rawValue = Evaluate(node.Arguments[0])?.ToString() ?? "";
            if (node.Method.Name == "Contains") { _Sql.Append(" LIKE "); AddParameter($"%{rawValue}%"); }
            else if (node.Method.Name == "StartsWith") { _Sql.Append(" LIKE "); AddParameter($"{rawValue}%"); }
            else if (node.Method.Name == "EndsWith") { _Sql.Append(" LIKE "); AddParameter($"%{rawValue}"); }
            return node;
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            AddParameter(node.Value);
            return node;
        }

        private bool IsNullConstant(Expression exp) => exp is ConstantExpression c && c.Value == null;

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
