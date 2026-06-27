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
        /// <summary>A char comparison that C# promoted to int==int: rebind the int code point as a 1-char string so
        /// it compares against the <c>character(1)</c> column. Lives here (not just in the visitor) so the
        /// cache-replay path reproduces the rebind instead of binding the raw int.</summary>
        CharString = 5,
    }

    internal static class WhereParameter
    {
        /// <summary>Applies <paramref name="transform"/> to a raw evaluated value to get the bound parameter value.</summary>
        public static object? Apply(ParamTransform transform, object? raw, Type? arrayElementType)
        {
            switch (transform)
            {
                // A null LIKE pattern would become '%%' / '%' and match EVERY row. The WHERE visitor throws for a
                // null argument during first translation, but the cache-replay path comes straight here, so the
                // guard must live here too (single source of truth) — otherwise a cached StartsWith/Contains shape
                // replayed with a null value silently returns all rows.
                case ParamTransform.LikeContains:
                case ParamTransform.LikeStartsWith:
                case ParamTransform.LikeEndsWith:
                    if (raw == null)
                        throw new System.ArgumentNullException("value",
                            "A null argument to string.Contains/StartsWith/EndsWith cannot be translated to SQL — " +
                            "it would match every row. Pass a non-null value or use an explicit IS NULL predicate.");
                    string __like = EscapeLike(raw.ToString() ?? "");
                    return transform == ParamTransform.LikeContains ? "%" + __like + "%"
                        : transform == ParamTransform.LikeStartsWith ? __like + "%"
                        : "%" + __like;
                case ParamTransform.TypedArray:
                    return ExpressionEvaluator.ToTypedArray(raw, arrayElementType!);
                case ParamTransform.CharString:
                    // raw is the int code point (the char promoted to int by C#); rebind it as a 1-char string.
                    return raw == null ? null : ((char)System.Convert.ToInt32(raw)).ToString();
                default:
                    return Normalize(raw);
            }
        }

        /// <summary>
        /// Normalizes a value bound into a WHERE/predicate parameter the same way the insert/update write paths
        /// do, so a filter compares correctly against the stored value: enums as their underlying integral type;
        /// a Kind=Utc DateTime relabeled Unspecified (a 'timestamp' column is naive, and Npgsql would otherwise
        /// infer 'timestamptz' and shift by the session TimeZone, matching the wrong rows); a non-zero-offset
        /// DateTimeOffset normalized to UTC (Npgsql rejects a non-UTC offset for 'timestamptz'); and unsigned
        /// CLR types widened (Npgsql has no wire mapping for them).
        /// </summary>
        public static object? Normalize(object? value)
        {
            if (value is Enum e)
                return Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()));
            if (value is DateTime dt && dt.Kind == DateTimeKind.Utc)
                return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            if (value is DateTimeOffset dto && dto.Offset != TimeSpan.Zero)
                return dto.ToUniversalTime();
            if (value is ushort us) return (int)us;
            if (value is uint ui) return (long)ui;
            if (value is ulong ul) return (decimal)ul;
            return value;
        }

        /// <summary>Escapes LIKE wildcards so user values match literally (used with <c>ESCAPE '\'</c>).</summary>
        public static string EscapeLike(string value) =>
            value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }
#nullable disable
}
