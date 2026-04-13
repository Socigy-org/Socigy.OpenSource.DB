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

        public PostgresqlWhereVisitor(ParameterExpression rowParam, GetColumnName getColumnName, DbCommand command)
        {
            _rowParam = rowParam;
            _GetColumnName = getColumnName;
            _Command = command;
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
        // 1. Method Calls - The Core Logic Change
        // ---------------------------------------------------------
        protected override Expression VisitUnary(UnaryExpression node)
        {
            // A. Partial Evaluation: e.g. (int)UserVisibility.Public
            // If this entire cast can be run locally, do it now.
            if (TryEvaluate(node, out var value))
            {
                AddParameter(value);
                return node;
            }

            // B. Logic: NOT
            if (node.NodeType == ExpressionType.Not)
            {
                _Sql.Append(" NOT (");
                Visit(node.Operand);
                _Sql.Append(")");
                return node;
            }

            // C. SQL-side Conversions
            // If we have (int)x.Column, we usually just ignore the cast in SQL 
            // or let the DB handle the type promotion.
            if (node.NodeType == ExpressionType.Convert || node.NodeType == ExpressionType.ConvertChecked)
            {
                Visit(node.Operand);
                return node;
            }

            return base.VisitUnary(node);
        }

        // -------------------------------------------------------------------------
        // 2. Binary Expressions (Logic & Comparisons)
        // -------------------------------------------------------------------------
        protected override Expression VisitBinary(BinaryExpression node)
        {
            // A. Partial Evaluation (Calculated values)
            // Check if the WHOLE binary node can be evaluated (e.g. 1 + 2)
            // This is rare in a WHERE clause top-level, but good for nested math.
            if (TryEvaluate(node, out var value))
            {
                AddParameter(value);
                return node;
            }

            // B. Null Checks
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
            // If it's the DB Row (x.Visibility)
            if (node.Expression == _rowParam)
            {
                _Sql.Append(_GetColumnName(node.Member.Name));
                return node;
            }

            // If it's a local variable, static property, or Enum
            if (TryEvaluate(node, out var value))
            {
                AddParameter(value);
                return node;
            }

            // If we reach here, it's a MemberAccess we couldn't evaluate and isn't the row.
            // This usually implies a complex chain we failed to parse.
            // We fall back to base, which does NOTHING to _Sql, causing the empty string bug.
            // To be safe, we can try to throw or debug, but usually TryEvaluate covers 99% of cases.
            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // A. Skip Evaluation for SQL Markers
            if (IsSqlMarker(node)) return VisitSqlMarkers(node);

            // B. String SQL Methods
            if (IsDependentOnParam(node) && node.Method.DeclaringType == typeof(string))
                return HandleStringMethods(node);

            // C. Partial Evaluation (Guid.NewGuid())
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
            // Optimization: Don't try to compile/invoke if it obviously touches 'x'
            if (IsDependentOnParam(e))
            {
                result = null;
                return false;
            }
            try
            {
                result = Evaluate(e);
                return true;
            }
            catch
            {
                result = null;
                return false;
            }
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
