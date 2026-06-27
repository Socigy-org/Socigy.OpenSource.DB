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

        protected override Expression VisitNewArray(NewArrayExpression node)
        {
            for (int i = 0; i < node.Expressions.Count; i++)
            {
                if (i > 0) _Sql.Append(", ");
                Visit(node.Expressions[i]);
            }
            return node;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == _rowParam)
            {
                _Sql.Append('"').Append(_GetColumnName(node.Member.Name)).Append('"');
                return node;
            }

            if (TryEvaluate(node, out var value))
            {
                AddParameter(value);
                return node;
            }

            return base.VisitMember(node);
        }

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

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(string))
            {
                return HandleStringMethods(node);
            }

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
                    case "End":
                        // Explicit terminator for an ELSE-less CASE (valid SQL: yields NULL when no WHEN matches).
                        // Without this case the CASE block was never closed, producing malformed SQL. If the inner
                        // call was Else(), it already appended END, so don't double-close.
                        if (!(node.Object is MethodCallExpression __innerEnd && __innerEnd.Method.Name == "Else"))
                            _Sql.Append(" END");
                        break;
                    case "As":
                        // "As" is likely called on the result of Else, or checking a fluent terminator
                        if (TryEvaluate(node.Arguments[0], out var alias))
                            _Sql.Append($" AS \"{alias}\"");
                        break;
                }
                return node;
            }

            // A param-independent method call (a captured helper) folds to a single value.
            if (TryEvaluate(node, out var evaluated))
            {
                AddParameter(evaluated);
                return node;
            }

            // A column-dependent, unsupported method call (e.g. `x.Created.AddDays(1)`) would otherwise fall to
            // base.VisitMethodCall, which emits only the bare column and silently drops the call — a wrong
            // projection. Fail fast like VisitBinary and the WHERE/ORDER BY visitors do.
            throw new NotSupportedException(
                $"Unsupported method call '{node.Method.Name}' in SQL SELECT translation: {node}");
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            // char comparison (e.g. a projected `Case().When(x.Initial == 'A')`): C# promotes char==char to
            // int==int, so without this the int code point (65) binds against the character(1) column
            // ("character = integer"). Bind the value back as a 1-char string. Mirrors the WHERE visitor.
            if (IsCharComparisonOperator(node.NodeType))
            {
                bool leftChar = IsCharPromotion(node.Left);
                bool rightChar = IsCharPromotion(node.Right);
                if (leftChar ^ rightChar)
                {
                    _Sql.Append("(");
                    EmitCharComparisonOperand(node.Left, leftChar);
                    _Sql.Append(CharComparisonOperatorSql(node.NodeType));
                    EmitCharComparisonOperand(node.Right, rightChar);
                    _Sql.Append(")");
                    return node;
                }
            }

            _Sql.Append("(");
            Visit(node.Left);
            switch (node.NodeType)
            {
                case ExpressionType.Equal: _Sql.Append(" = "); break;
                case ExpressionType.AndAlso: _Sql.Append(" AND "); break;
                case ExpressionType.OrElse: _Sql.Append(" OR "); break;
                case ExpressionType.NotEqual: _Sql.Append(" != "); break;
                case ExpressionType.GreaterThan: _Sql.Append(" > "); break;
                case ExpressionType.GreaterThanOrEqual: _Sql.Append(" >= "); break;
                case ExpressionType.LessThan: _Sql.Append(" < "); break;
                case ExpressionType.LessThanOrEqual: _Sql.Append(" <= "); break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported binary operator '{node.NodeType}' in SQL SELECT translation: {node}");
            }
            Visit(node.Right);
            _Sql.Append(")");
            return node;
        }

        private static bool IsCharPromotion(Expression e) =>
            e is UnaryExpression u && (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked)
                && u.Operand.Type == typeof(char);

        private static bool IsCharComparisonOperator(ExpressionType t) =>
            t is ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan
              or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

        private static string CharComparisonOperatorSql(ExpressionType t) => t switch
        {
            ExpressionType.Equal => " = ",
            ExpressionType.NotEqual => " != ",
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

        protected override Expression VisitConstant(ConstantExpression node)
        {
            AddParameter(node.Value);
            return node;
        }

        // An inline constructor in a projection (e.g. `new object[] { x.Id, new DateOnly(2020,1,1) }`, or a
        // `new Guid("...")` in a CASE Then/Else) must fold to a SINGLE parameter. Without this, the base visitor
        // recurses into the constructor arguments, emitting one @p per arg with no separator ("@p0@p1@p2",
        // invalid SQL) or binding a single-arg ctor's string instead of the Guid. Mirrors the WHERE visitor.
        protected override Expression VisitNew(NewExpression node)
        {
            if (TryEvaluate(node, out var value)) { AddParameter(value); return node; }
            throw new NotSupportedException(
                $"Unsupported constructor '{node.Type.Name}' in SQL SELECT translation: {node}");
        }

        private Expression HandleStringMethods(MethodCallExpression node)
        {
            string name = node.Method.Name;

            // Bare ToLower()/ToUpper(): wrap the column, matching the WHERE visitor. Previously these (and any
            // other unrecognized string method) fell through and emitted just the bare column, silently dropping
            // the transform (e.g. a case-insensitive compare became case-sensitive).
            if (name == "ToLower" || name == "ToUpper")
            {
                _Sql.Append(name == "ToLower" ? "LOWER(" : "UPPER(");
                Visit(node.Object);
                _Sql.Append(")");
                return node;
            }

            if (name == "Contains" || name == "StartsWith" || name == "EndsWith")
            {
                // A null pattern would become LIKE '%%' and match every row — fail fast, matching the WHERE visitor.
                var arg = Evaluate(node.Arguments[0]);
                if (arg == null)
                    throw new ArgumentNullException("value",
                        $"A null argument to string.{name} cannot be translated to SQL — it would match every row. " +
                        "Pass a non-null value or use an explicit IS NULL predicate.");

                Visit(node.Object);

                // Escape LIKE wildcards in the literal so a value containing % or _ matches literally, matching
                // the WHERE visitor (otherwise e.g. Contains("50%") would match anything).
                var rawValue = global::Socigy.OpenSource.DB.Core.Parsers.WhereParameter.EscapeLike(arg.ToString() ?? "");
                _Sql.Append(" LIKE ");
                AddParameter(name == "Contains" ? $"%{rawValue}%" : name == "StartsWith" ? $"{rawValue}%" : $"%{rawValue}");
                _Sql.Append(" ESCAPE '\\'");
                return node;
            }

            throw new NotSupportedException($"Unsupported string method '{name}' in SQL SELECT translation: {node}");
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

        // Reflection-light evaluation of parameter-independent closures (constants / captured vars).
        private object? Evaluate(Expression e) => ExpressionEvaluator.Evaluate(e);

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
