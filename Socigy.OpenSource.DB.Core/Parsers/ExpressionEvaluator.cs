using System;
using System.Linq.Expressions;
using System.Reflection;

namespace Socigy.OpenSource.DB.Core.Parsers
{
#nullable enable
    /// <summary>
    /// Evaluates parameter-independent sub-expressions (constants and captured variables) when
    /// translating LINQ predicates to SQL. The common cases — constant literals and field/property
    /// access chains (closure captures) — are read directly via reflection (<see cref="FieldInfo.GetValue"/>
    /// / <see cref="PropertyInfo.GetValue"/>) with no IL code generation. Only shapes that cannot be read
    /// directly (method calls, arithmetic among captures, indexers) fall back to
    /// <see cref="Expression.Lambda(Expression, ParameterExpression[])"/> + <c>Compile().DynamicInvoke()</c>.
    ///
    /// This keeps the hot path off <c>Expression.Compile()</c> (slow, and interpreted/limited under
    /// NativeAOT). It does not eliminate reflection entirely — reading an arbitrary captured local from a
    /// compiler-generated closure requires either reflection or codegen — but it removes runtime IL
    /// emission from the overwhelmingly common predicate shapes.
    /// </summary>
    internal static class ExpressionEvaluator
    {
        /// <summary>Evaluates a parameter-independent expression to its runtime value.</summary>
        public static object? Evaluate(Expression? expression)
        {
            switch (expression)
            {
                case null:
                    return null;

                case ConstantExpression constant:
                    return constant.Value;

                case MemberExpression member when member.Member is FieldInfo field:
                {
                    object? target = field.IsStatic ? null : Evaluate(member.Expression);
                    return field.GetValue(target);
                }

                case MemberExpression member when member.Member is PropertyInfo property:
                {
                    MethodInfo? getter = property.GetGetMethod(nonPublic: true);
                    object? target = (getter != null && getter.IsStatic) ? null : Evaluate(member.Expression);
                    return property.GetValue(target);
                }

                default:
                    // Method calls, arithmetic among captured values, converts, indexers, etc.
                    return Expression.Lambda(expression).Compile().DynamicInvoke();
            }
        }

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
