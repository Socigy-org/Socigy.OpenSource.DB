using System;

namespace Socigy.OpenSource.DB.Core.Parsers
{
#nullable enable
    /// <summary>
    /// How a raw evaluated value is turned into the bound parameter value. Shared by the WHERE visitor
    /// (translation) and the <see cref="QueryShapeCache"/> replay so both produce identical values from
    /// the same source expression — there is exactly one place this logic lives.
    /// </summary>
    internal enum ParamTransform
    {
        /// <summary>Use the value as-is (enums normalized to their underlying type).</summary>
        Value = 0,
        /// <summary><c>%value%</c> with LIKE wildcards escaped.</summary>
        LikeContains = 1,
        /// <summary><c>value%</c> with LIKE wildcards escaped.</summary>
        LikeStartsWith = 2,
        /// <summary><c>%value</c> with LIKE wildcards escaped.</summary>
        LikeEndsWith = 3,
        /// <summary>Materialize an <see cref="System.Collections.IEnumerable"/> into a typed array for <c>= ANY(@p)</c>.</summary>
        TypedArray = 4,
    }

    internal static class WhereParameter
    {
        /// <summary>Applies <paramref name="transform"/> to a raw evaluated value to get the bound parameter value.</summary>
        public static object? Apply(ParamTransform transform, object? raw, Type? arrayElementType)
        {
            switch (transform)
            {
                case ParamTransform.LikeContains:
                    return "%" + EscapeLike(raw?.ToString() ?? "") + "%";
                case ParamTransform.LikeStartsWith:
                    return EscapeLike(raw?.ToString() ?? "") + "%";
                case ParamTransform.LikeEndsWith:
                    return "%" + EscapeLike(raw?.ToString() ?? "");
                case ParamTransform.TypedArray:
                    return ExpressionEvaluator.ToTypedArray(raw, arrayElementType!);
                default:
                    return Normalize(raw);
            }
        }

        /// <summary>Enums are bound as their underlying integral type.</summary>
        public static object? Normalize(object? value)
        {
            if (value is Enum e)
                return Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()));
            return value;
        }

        /// <summary>Escapes LIKE wildcards so user values match literally (used with <c>ESCAPE '\'</c>).</summary>
        public static string EscapeLike(string value) =>
            value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }
#nullable disable
}
