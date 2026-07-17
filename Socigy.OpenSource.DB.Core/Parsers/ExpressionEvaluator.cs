using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace Socigy.OpenSource.DB.Core.Parsers
{
#nullable enable
    /// <summary>
    /// Evaluates sub-expressions to their runtime value WITHOUT <c>Expression.Compile()</c> — entirely via
    /// reflection — so it works under NativeAOT (where <c>Compile</c> is <c>[RequiresDynamicCode]</c>). Used to
    /// fold parameter-independent operands (constants and captured variables) when translating LINQ predicates to
    /// SQL, and (via <see cref="EvaluateWithParameter"/>) to evaluate a small boolean test against a concrete entity
    /// for a selective-update <c>WithFields</c> conditional. Handles the realistic shapes — constants, field/property
    /// chains, method calls, converts, indexers, ternaries, <c>new</c>/array literals, and the common operators;
    /// a genuinely exotic shape throws a clear, actionable error rather than silently producing dynamic code.
    /// </summary>
    internal static class ExpressionEvaluator
    {
        /// <summary>Evaluates a parameter-independent expression to its runtime value.</summary>
        public static object? Evaluate(Expression? expression) => Eval(expression, null, null);

        /// <summary>
        /// Evaluates <paramref name="expression"/> with the single parameter <paramref name="parameter"/> bound to
        /// <paramref name="parameterValue"/> — used to interpret a row-dependent boolean test against a concrete
        /// entity (replacing an <c>Expression.Compile().DynamicInvoke(entity)</c>).
        /// </summary>
        public static object? EvaluateWithParameter(Expression? expression, ParameterExpression parameter, object? parameterValue)
            => Eval(expression, parameter, parameterValue);

        private static object? Eval(Expression? expression, ParameterExpression? param, object? paramValue)
        {
            switch (expression)
            {
                case null:
                    return null;

                case ConstantExpression constant:
                    return constant.Value;

                case ParameterExpression p when p == param:
                    return paramValue;

                case MemberExpression member when member.Member is FieldInfo field:
                {
                    object? target = field.IsStatic ? null : Eval(member.Expression, param, paramValue);
                    return field.GetValue(target);
                }

                case MemberExpression member when member.Member is PropertyInfo property:
                {
                    MethodInfo? getter = property.GetGetMethod(nonPublic: true);
                    object? target = (getter != null && getter.IsStatic) ? null : Eval(member.Expression, param, paramValue);
                    return property.GetValue(target);
                }

                case UnaryExpression unary:
                    return EvalUnary(unary, param, paramValue);

                case BinaryExpression binary:
                    return EvalBinary(binary, param, paramValue);

                case MethodCallExpression call:
                {
                    object? instance = call.Object == null ? null : Eval(call.Object, param, paramValue);
                    object?[] args = EvalArgs(call.Arguments, param, paramValue);
                    return call.Method.Invoke(instance, args);
                }

                case ConditionalExpression cond:
                    return Eval(cond.Test, param, paramValue) is true
                        ? Eval(cond.IfTrue, param, paramValue)
                        : Eval(cond.IfFalse, param, paramValue);

                case NewExpression ne:
                    return ne.Constructor == null
                        ? Activator.CreateInstance(ne.Type)
                        : ne.Constructor.Invoke(EvalArgs(ne.Arguments, param, paramValue));

                case NewArrayExpression na when na.NodeType == ExpressionType.NewArrayInit:
                {
                    Type elem = na.Type.GetElementType()!;
                    Array arr = Array.CreateInstance(elem, na.Expressions.Count);
                    for (int i = 0; i < na.Expressions.Count; i++)
                        arr.SetValue(Eval(na.Expressions[i], param, paramValue), i);
                    return arr;
                }

                case IndexExpression idx:
                {
                    object? target = Eval(idx.Object, param, paramValue);
                    object?[] args = EvalArgs(idx.Arguments, param, paramValue);
                    return idx.Indexer != null
                        ? idx.Indexer.GetValue(target, args)
                        : ((Array)target!).GetValue(System.Array.ConvertAll(args, a => Convert.ToInt32(a)));
                }

                default:
                    throw new NotSupportedException(
                        $"Cannot evaluate the expression '{expression}' (node {expression.NodeType}) without dynamic code. " +
                        "Hoist this value into a local variable before the query (it is evaluated at translation time anyway).");
            }
        }

        private static object?[] EvalArgs(System.Collections.ObjectModel.ReadOnlyCollection<Expression> args, ParameterExpression? param, object? paramValue)
        {
            if (args.Count == 0)
                return Array.Empty<object?>();
            var values = new object?[args.Count];
            for (int i = 0; i < args.Count; i++)
                values[i] = Eval(args[i], param, paramValue);
            return values;
        }

        private static object? EvalUnary(UnaryExpression unary, ParameterExpression? param, object? paramValue)
        {
            // A user-defined conversion/operator carries its method; invoke it directly.
            if (unary.Method != null)
                return unary.Method.Invoke(null, new[] { Eval(unary.Operand, param, paramValue) });

            object? operand = Eval(unary.Operand, param, paramValue);
            switch (unary.NodeType)
            {
                case ExpressionType.Convert:
                case ExpressionType.ConvertChecked:
                case ExpressionType.TypeAs:
                {
                    Type target = Nullable.GetUnderlyingType(unary.Type) ?? unary.Type;
                    if (operand == null) return null;
                    if (target.IsInstanceOfType(operand)) return operand;       // boxing/identity (e.g. -> object)
                    if (target.IsEnum) return Enum.ToObject(target, operand);
                    if (operand is IConvertible)
                    {
                        // A C# cast from a floating/decimal value to an integral type truncates toward zero;
                        // Convert.ChangeType would ROUND (e.g. (int)3.7 -> 4 instead of 3). Pre-truncate so the
                        // folded value matches what the compiled cast produced.
                        if (IsIntegralType(target))
                        {
                            if (operand is double dbl) operand = Math.Truncate(dbl);
                            else if (operand is float flt) operand = Math.Truncate((double)flt);
                            else if (operand is decimal dec) operand = Math.Truncate(dec);
                        }
                        return Convert.ChangeType(operand, target);
                    }
                    return operand;
                }
                case ExpressionType.Not:
                    // Bitwise complement (or logical NOT on bool). Preserve the C# operand type: ~ on a type narrower
                    // than int promotes to int, while uint/long/ulong keep their own type and value (routing through
                    // Convert.ToInt64 both mis-typed the result and corrupted unsigned values).
                    if (operand == null) return null;
                    switch (operand)
                    {
                        case bool b: return !b;
                        case ulong ul: return ~ul;
                        case long l: return ~l;
                        case uint u: return ~u;
                        case int i: return ~i;
                        default: return ~Convert.ToInt32(operand);   // sbyte/byte/short/ushort/char -> int
                    }
                case ExpressionType.Negate:
                case ExpressionType.NegateChecked:
                    // Preserve the C# result type rather than collapsing everything to double (which changes the
                    // bound parameter's type and loses precision for large long values).
                    if (operand == null) return null;
                    switch (operand)
                    {
                        case decimal dm: return -dm;
                        case double d: return -d;
                        case float f: return -f;
                        case long l: return -l;
                        case uint u: return -(long)u;                 // -uint promotes to long in C#
                        case int i: return -i;
                        default: return -Convert.ToInt32(operand);    // sbyte/byte/short/ushort/char -> int
                    }
                case ExpressionType.Quote:
                    return ((UnaryExpression)unary).Operand;
                default:
                    throw new NotSupportedException($"Unsupported unary operator '{unary.NodeType}' in AOT expression evaluation.");
            }
        }

        private static object? EvalBinary(BinaryExpression binary, ParameterExpression? param, object? paramValue)
        {
            // Short-circuit logical operators must not eagerly evaluate the right operand.
            if (binary.NodeType == ExpressionType.AndAlso)
                return Eval(binary.Left, param, paramValue) is true && Eval(binary.Right, param, paramValue) is true;
            if (binary.NodeType == ExpressionType.OrElse)
                return Eval(binary.Left, param, paramValue) is true || Eval(binary.Right, param, paramValue) is true;
            if (binary.NodeType == ExpressionType.Coalesce)
                return Eval(binary.Left, param, paramValue) ?? Eval(binary.Right, param, paramValue);

            object? left = Eval(binary.Left, param, paramValue);
            object? right = Eval(binary.Right, param, paramValue);

            // A user-defined operator carries its method; invoke it directly (correct semantics for custom types).
            if (binary.Method != null)
                return binary.Method.Invoke(null, new[] { left, right });

            switch (binary.NodeType)
            {
                case ExpressionType.ArrayIndex:
                    return ((Array)left!).GetValue(Convert.ToInt32(right));
                case ExpressionType.Equal:
                    return Equals(left, right);
                case ExpressionType.NotEqual:
                    return !Equals(left, right);
                case ExpressionType.And:
                case ExpressionType.Or:
                case ExpressionType.ExclusiveOr:
                    // A bool operand means logical (non-short-circuit) &/|/^; otherwise it is an integral bitwise op.
                    // Bool uses three-valued (lifted-nullable) logic so e.g. `true & (bool?)null` is null, not an NRE.
                    return (left is bool || right is bool)
                        ? BoolLogic(binary.NodeType, left, right)
                        : IntegralBitwise(binary.NodeType, left, right);
            }

            // String concatenation.
            if (binary.NodeType == ExpressionType.Add && (left is string || right is string))
                return string.Concat(left, right);

            // Relational / arithmetic on comparable / numeric operands. Param-independent arithmetic among captures
            // is rare in a predicate (typically hoisted to a local); decimal/double covers the realistic cases.
            switch (binary.NodeType)
            {
                case ExpressionType.LessThan:
                case ExpressionType.LessThanOrEqual:
                case ExpressionType.GreaterThan:
                case ExpressionType.GreaterThanOrEqual:
                {
                    // A lifted relational comparison with a null operand is false in C# (e.g. (int?)null < 5 == false).
                    if (left == null || right == null) return false;
                    int cmp = CompareValues(left, right);
                    return binary.NodeType switch
                    {
                        ExpressionType.LessThan => cmp < 0,
                        ExpressionType.LessThanOrEqual => cmp <= 0,
                        ExpressionType.GreaterThan => cmp > 0,
                        _ => cmp >= 0,
                    };
                }
            }

            // Arithmetic, following C# numeric promotion so the RESULT TYPE matches what the compiled code would
            // produce (int + int -> int, byte + byte -> int, float + int -> float, ulong + ulong -> ulong, ...):
            // otherwise the bound parameter's CLR type — and thus the inferred PG type — changes.
            return ArithmeticOp(binary.NodeType, left, right);
        }

        // The integral/floating kind two operands promote to under C# binary numeric promotion.
        private enum NumKind { Int, UInt, Long, ULong, Float, Double, Decimal }

        private static NumKind Promote(object? l, object? r)
        {
            if (l is decimal || r is decimal) return NumKind.Decimal;
            if (l is double || r is double) return NumKind.Double;
            if (l is float || r is float) return NumKind.Float;
            if (l is ulong || r is ulong) return NumKind.ULong;
            if (l is long || r is long) return NumKind.Long;
            if (l is uint || r is uint)
            {
                // uint combined with a signed int/short/sbyte promotes to long; with another unsigned/narrow type it stays uint.
                bool signed = l is sbyte or short or int || r is sbyte or short or int;
                return signed ? NumKind.Long : NumKind.UInt;
            }
            return NumKind.Int;   // int and any narrower integral (byte/sbyte/short/ushort/char) promote to int
        }

        private static object? ArithmeticOp(ExpressionType op, object? left, object? right)
        {
            // Lifted nullable arithmetic: any null operand yields null (e.g. (int?)null + 5 == null).
            if (left == null || right == null) return null;
            // `unchecked` matches the default (non-checked) arithmetic the Add/Subtract/... nodes represent.
            switch (Promote(left, right))
            {
                case NumKind.Decimal: { decimal a = Convert.ToDecimal(left), b = Convert.ToDecimal(right); return op switch { ExpressionType.Add => a + b, ExpressionType.Subtract => a - b, ExpressionType.Multiply => a * b, ExpressionType.Divide => a / b, ExpressionType.Modulo => a % b, _ => throw Unsupported(op) }; }
                case NumKind.Double: { double a = Convert.ToDouble(left), b = Convert.ToDouble(right); return op switch { ExpressionType.Add => a + b, ExpressionType.Subtract => a - b, ExpressionType.Multiply => a * b, ExpressionType.Divide => a / b, ExpressionType.Modulo => a % b, _ => throw Unsupported(op) }; }
                case NumKind.Float: { float a = Convert.ToSingle(left), b = Convert.ToSingle(right); return op switch { ExpressionType.Add => a + b, ExpressionType.Subtract => a - b, ExpressionType.Multiply => a * b, ExpressionType.Divide => a / b, ExpressionType.Modulo => a % b, _ => throw Unsupported(op) }; }
                case NumKind.ULong: { ulong a = Convert.ToUInt64(left), b = Convert.ToUInt64(right); unchecked { return op switch { ExpressionType.Add => a + b, ExpressionType.Subtract => a - b, ExpressionType.Multiply => a * b, ExpressionType.Divide => a / b, ExpressionType.Modulo => a % b, _ => throw Unsupported(op) }; } }
                case NumKind.Long: { long a = Convert.ToInt64(left), b = Convert.ToInt64(right); unchecked { return op switch { ExpressionType.Add => a + b, ExpressionType.Subtract => a - b, ExpressionType.Multiply => a * b, ExpressionType.Divide => a / b, ExpressionType.Modulo => a % b, _ => throw Unsupported(op) }; } }
                case NumKind.UInt: { uint a = Convert.ToUInt32(left), b = Convert.ToUInt32(right); unchecked { return op switch { ExpressionType.Add => a + b, ExpressionType.Subtract => a - b, ExpressionType.Multiply => a * b, ExpressionType.Divide => a / b, ExpressionType.Modulo => a % b, _ => throw Unsupported(op) }; } }
                default: { int a = Convert.ToInt32(left), b = Convert.ToInt32(right); unchecked { return op switch { ExpressionType.Add => a + b, ExpressionType.Subtract => a - b, ExpressionType.Multiply => a * b, ExpressionType.Divide => a / b, ExpressionType.Modulo => a % b, _ => throw Unsupported(op) }; } }
            }
        }

        // Three-valued (lifted-nullable) logical &/|/^ on bool?: false dominates &, true dominates |, and any other
        // null yields null. Matches C# `bool? op bool?` (e.g. false & null == false, true | null == true, x ^ null == null).
        private static object? BoolLogic(ExpressionType op, object? left, object? right)
        {
            bool? l = (bool?)left, r = (bool?)right;
            switch (op)
            {
                case ExpressionType.And:
                    if (l == false || r == false) return false;
                    return (l == null || r == null) ? (object?)null : true;
                case ExpressionType.Or:
                    if (l == true || r == true) return true;
                    return (l == null || r == null) ? (object?)null : false;
                default: // ExclusiveOr
                    return (l == null || r == null) ? (object?)null : (l.Value ^ r.Value);
            }
        }

        private static object? IntegralBitwise(ExpressionType op, object? left, object? right)
        {
            // Lifted nullable bitwise: any null operand yields null.
            if (left == null || right == null) return null;
            switch (Promote(left, right))
            {
                case NumKind.ULong: { ulong a = Convert.ToUInt64(left), b = Convert.ToUInt64(right); return op switch { ExpressionType.And => a & b, ExpressionType.Or => a | b, ExpressionType.ExclusiveOr => a ^ b, _ => throw Unsupported(op) }; }
                case NumKind.Long: { long a = Convert.ToInt64(left), b = Convert.ToInt64(right); return op switch { ExpressionType.And => a & b, ExpressionType.Or => a | b, ExpressionType.ExclusiveOr => a ^ b, _ => throw Unsupported(op) }; }
                case NumKind.UInt: { uint a = Convert.ToUInt32(left), b = Convert.ToUInt32(right); return op switch { ExpressionType.And => a & b, ExpressionType.Or => a | b, ExpressionType.ExclusiveOr => a ^ b, _ => throw Unsupported(op) }; }
                default: { int a = Convert.ToInt32(left), b = Convert.ToInt32(right); return op switch { ExpressionType.And => a & b, ExpressionType.Or => a | b, ExpressionType.ExclusiveOr => a ^ b, _ => throw Unsupported(op) }; }
            }
        }

        private static bool IsIntegralType(Type t)
            => t == typeof(sbyte) || t == typeof(byte) || t == typeof(short) || t == typeof(ushort)
            || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);

        private static NotSupportedException Unsupported(ExpressionType nodeType)
            => new NotSupportedException(
                $"Unsupported binary operator '{nodeType}' in AOT expression evaluation. Hoist the value into a local variable before the query.");

        // Orders two evaluated operands. Numeric operands compare numerically (across int/long/double/etc.);
        // other comparables (DateTime, string, same-typed values) use the default comparer.
        private static int CompareValues(object? left, object? right)
        {
            if (left == null) return right == null ? 0 : -1;
            if (right == null) return 1;
            if (IsNumeric(left) && IsNumeric(right))
                return Convert.ToDouble(left).CompareTo(Convert.ToDouble(right));
            return Comparer<object>.Default.Compare(left, right);
        }

        private static bool IsNumeric(object value) => value is sbyte or byte or short or ushort or int or uint
            or long or ulong or float or double or decimal;

        /// <summary>
        /// Materializes an evaluated collection into a strongly-typed array (for PostgreSQL
        /// <c>= ANY(@p)</c>), using the element type known from the expression — avoiding a runtime
        /// <c>GetType().GetInterfaces()</c> scan.
        /// </summary>
        public static object? ToTypedArray(object? value, Type elementType)
        {
            if (value == null)
                return value;
            if (value is string || value is not System.Collections.IEnumerable enumerable)
                return value;

            // Normalize each element the same way the scalar `= @p` path does (WhereParameter.Normalize): an enum
            // becomes its underlying integer, unsigned types widen, a Kind=Utc DateTime is relabeled Unspecified,
            // and a non-UTC DateTimeOffset is shifted to UTC. The array's element type must match the NORMALIZED
            // element (e.g. uint[] -> long[], enum[] -> underlying[]) so `= ANY(@p)` binds and compares correctly.
            // (Npgsql has no wire mapping for unsigned/enum arrays, and an un-relabeled DateTime[]/DateTimeOffset[]
            // would shift or throw — exactly the scalar bug, but for the array form.)
            Type normalizedElementType = NormalizeArrayElementType(elementType);

            var items = new System.Collections.ArrayList();
            foreach (var item in enumerable)
                items.Add(WhereParameter.Normalize(item));

            Array array = Array.CreateInstance(normalizedElementType, items.Count);
            items.CopyTo(array);
            return array;
        }

        // The array element type after WhereParameter.Normalize. Mirrors its scalar rules: enum -> underlying,
        // ushort -> int, uint -> long, ulong -> decimal. DateTime/DateTimeOffset keep their type (only the value
        // is relabeled/shifted), so the array stays DateTime[]/DateTimeOffset[].
        // A Nullable<T> element (e.g. a List<Role?> / List<uint?> bound against a nullable column, which may also
        // contain a null) is unwrapped, normalized, then re-wrapped as Nullable<normalized> — otherwise the array
        // type stayed Role?/uint? while Normalize produced an int/long, and CopyTo threw InvalidCastException.
        private static Type NormalizeArrayElementType(Type elementType)
        {
            Type? nullableUnderlying = Nullable.GetUnderlyingType(elementType);
            Type core = nullableUnderlying ?? elementType;

            Type normalizedCore =
                core.IsEnum ? Enum.GetUnderlyingType(core) :
                core == typeof(ushort) ? typeof(int) :
                core == typeof(uint) ? typeof(long) :
                core == typeof(ulong) ? typeof(decimal) :
                core;

            // Preserve nullability so an array that may contain nulls can still hold them.
            if (nullableUnderlying != null && normalizedCore.IsValueType)
                return typeof(Nullable<>).MakeGenericType(normalizedCore);
            return normalizedCore;
        }
    }
#nullable disable
}
