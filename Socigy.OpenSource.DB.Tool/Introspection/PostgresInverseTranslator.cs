using System;
using System.Collections.Generic;
using Socigy.OpenSource.DB.Attributes;

namespace Socigy.OpenSource.DB.Tool.Introspection
{
    /// <summary>
    /// Inverse of the forward maps in <c>PostgreSqlGenerator</c>: turns live-database artifacts (column
    /// defaults, foreign-key actions, SQL types) back into the Socigy tokens / CLR types the analyzer emits.
    /// Keeping these as the exact inverse of <c>TranslateDefault</c> / <c>TranslateForeignKeyAction</c> /
    /// <c>CSharpTypeMapping</c> is what makes assembly → schema → assembly round-tripping stable.
    /// </summary>
    internal static class PostgresInverseTranslator
    {
        /// <summary>
        /// Maps a database <c>column_default</c> expression back to a <see cref="DbDefaults"/> token, or
        /// returns the (cast-stripped) literal unchanged when it isn't a recognized generated default.
        /// Returns null for an empty default or a <c>nextval(...)</c> (handled as auto-increment instead).
        /// </summary>
        public static string? InverseDefault(string? columnDefault)
        {
            if (string.IsNullOrWhiteSpace(columnDefault))
                return null;

            string normalized = StripCasts(columnDefault!).Trim();
            string lower = normalized.ToLowerInvariant();

            if (lower.StartsWith("nextval("))
                return null; // auto-increment, not a [Default]

            switch (lower)
            {
                case "gen_random_uuid()": return DbDefaults.Guid.Random;
                case "uuid_generate_v1mc()": return DbDefaults.Guid.Sequential;
                case "timezone('utc', now())": return DbDefaults.Time.Now;
                case "now()": return DbDefaults.Time.NowLocal;
                case "current_date": return DbDefaults.Time.Date;
                case "true": return DbDefaults.Bool.True;
                case "false": return DbDefaults.Bool.False;
                case "0": return DbDefaults.Number.Zero;
                case "1": return DbDefaults.Number.One;
                case "''": return DbDefaults.Text.Empty;
                default: return normalized; // pass through a literal default verbatim
            }
        }

        /// <summary>Maps a <c>pg_constraint</c> action code (a/r/c/n/d) back to a <see cref="DbValues.ForeignKey"/> token.</summary>
        public static string? InverseForeignKeyAction(char code)
        {
            switch (code)
            {
                case 'c': return DbValues.ForeignKey.Cascade;
                case 'n': return DbValues.ForeignKey.SetNull;
                case 'd': return DbValues.ForeignKey.SetDefault;
                case 'r': return DbValues.ForeignKey.Restrict;
                case 'a': return DbValues.ForeignKey.NoAction;
                default: return null;
            }
        }

        /// <summary>
        /// Maps a SQL type (<c>information_schema.columns.data_type</c>, with <paramref name="udtName"/> as a
        /// fallback) to the canonical CLR type the emitter should declare. The chosen CLR type is the one
        /// whose forward <c>CSharpTypeMapping</c> entry produces this SQL type, so the round-trip is stable.
        /// </summary>
        public static string PgTypeToCSharp(string dataType, string? udtName)
        {
            string t = (dataType ?? "").Trim().ToLowerInvariant();
            switch (t)
            {
                case "smallint": return "short";
                case "integer": return "int";
                case "bigint": return "long";
                case "numeric":
                case "decimal": return "decimal";
                case "double precision": return "double";
                case "real": return "float";
                case "boolean": return "bool";
                case "uuid": return "Guid";
                case "bytea": return "byte[]";
                case "jsonb":
                case "json": return "string"; // raw-JSON column; emitter adds [RawJsonColumn]
                case "text": return "string";
                case "character varying":
                case "varchar": return "string"; // length captured separately via [StringLength]
                case "character":
                case "char": return "char";
                case "timestamp without time zone": return "DateTime";
                case "timestamp with time zone": return "DateTimeOffset";
                case "date": return "DateOnly";
                case "time without time zone":
                case "time with time zone": return "TimeOnly";
                case "interval": return "TimeSpan";
            }

            // Fall back on the underlying type name for cases data_type reports generically.
            string u = (udtName ?? "").Trim().ToLowerInvariant();
            switch (u)
            {
                case "int2": return "short";
                case "int4": return "int";
                case "int8": return "long";
                case "float4": return "float";
                case "float8": return "double";
                case "bool": return "bool";
                case "timestamp": return "DateTime";
                case "timestamptz": return "DateTimeOffset";
                case "varchar": return "string";
            }

            return "string"; // safest default; unknown/USER-DEFINED types scaffold as text
        }

        /// <summary>Removes PostgreSQL <c>::type</c> casts (e.g. <c>'utc'::text</c> → <c>'utc'</c>,
        /// <c>'seq'::regclass</c> → <c>'seq'</c>) so defaults compare cleanly against the generator's output.</summary>
        private static string StripCasts(string expr)
            => System.Text.RegularExpressions.Regex.Replace(
                expr, @"::\s*""?[A-Za-z_][A-Za-z0-9_ ]*""?(\s*\([0-9, ]*\))?", "");
    }
}
