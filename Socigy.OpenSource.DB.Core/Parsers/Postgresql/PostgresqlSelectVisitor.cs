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

        // Handle the "new object[] { ... }" array initialization
        protected override Expression VisitNewArray(NewArrayExpression node)
        {
            for (int i = 0; i < node.Expressions.Count; i++)
            {
                if (i > 0) _Sql.Append(", ");
                Visit(node.Expressions[i]);
            }
            return node;
        }

        // Handle Member Access (e.g., x.Email -> "Email")
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == _rowParam)
            {
                // It's a column on our user x
                _Sql.Append(_GetColumnName(node.Member.Name));
                return node;
            }

            // It's a local variable capture or closure
            if (TryEvaluate(node, out var value))
            {
                AddParameter(value);
                return node;
            }

            return base.VisitMember(node);
        }

        // Handle Ternary Operators ( x ? y : z )
        protected override Expression VisitConditional(ConditionalExpression node)
        {
            // Check if the condition depends on the DB row (x)
            if (IsDependentOnParam(node.Test))
            {
                // It's a SQL CASE (based on DB column)
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
                // It's a local C# condition (username == "wailed")
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

        // Handle Method Calls (Select.Case, StartsWith, Custom, etc.)
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // A. Handle String LIKE operations
            if (node.Method.DeclaringType == typeof(string))
            {
                return HandleStringMethods(node);
            }

            // B. Handle Select.Custom("sql")
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

            // C. Handle Fluent API (Case/When/Then/Else/As)
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

        // Binary Operations (==, ||, &&)
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
                default: _Sql.Append($" {node.NodeType} "); break;
            }
            Visit(node.Right);
            _Sql.Append(")");
            return node;
        }

        // Constants
        protected override Expression VisitConstant(ConstantExpression node)
        {
            AddParameter(node.Value);
            return node;
        }

        private Expression HandleStringMethods(MethodCallExpression node)
        {
            // Process the column part first (e.g. x.Email)
            Visit(node.Object);

            // Get the value for the search
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

        // Helper to evaluate simple closures (like "wailed" or local vars)
        private object? Evaluate(Expression e)
        {
            if (e is ConstantExpression c) return c.Value;
            var lambda = Expression.Lambda(e);
            return lambda.Compile().DynamicInvoke();
        }

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
