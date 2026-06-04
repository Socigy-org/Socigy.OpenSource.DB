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
            if (value == null || value is Array)
                return value;
            if (value is not System.Collections.IEnumerable enumerable)
                return value;

            var items = new System.Collections.ArrayList();
            foreach (var item in enumerable)
                items.Add(item);

            Array array = Array.CreateInstance(elementType, items.Count);
            items.CopyTo(array);
            return array;
        }
    }
#nullable disable
}
