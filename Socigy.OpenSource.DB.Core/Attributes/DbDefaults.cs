using System;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Cross-platform sentinel constants for common column default values.
    /// Use with <see cref="DefaultAttribute"/>, e.g. <c>[Default(DbDefaults.Guid.Random)]</c>.
    /// Each constant is translated to the appropriate SQL expression by the target DB engine.
    /// </summary>
    public static class DbDefaults
    {
        internal const string Prefix = "$socigy$";

        public static class Guid
        {
            /// <summary>A randomly generated UUID (PostgreSQL: <c>gen_random_uuid()</c>).</summary>
            public const string Random = "$socigy$guid.random";

            /// <summary>A time-ordered UUID v1 (PostgreSQL: <c>uuid_generate_v1mc()</c>).</summary>
            public const string Sequential = "$socigy$guid.sequential";
        }

        public static class Time
        {
            /// <summary>Current UTC timestamp (PostgreSQL: <c>timezone('utc', now())</c>).</summary>
            public const string Now = "$socigy$time.now";

            /// <summary>Current local server timestamp (PostgreSQL: <c>now()</c>).</summary>
            public const string NowLocal = "$socigy$time.now.local";

            /// <summary>Current date only (PostgreSQL: <c>current_date</c>).</summary>
            public const string Date = "$socigy$time.date";
        }

        public static class Bool
        {
            /// <summary>Boolean true (PostgreSQL: <c>TRUE</c>).</summary>
            public const string True = "$socigy$bool.true";

            /// <summary>Boolean false (PostgreSQL: <c>FALSE</c>).</summary>
            public const string False = "$socigy$bool.false";
        }

        public static class Number
        {
            /// <summary>Numeric zero (PostgreSQL: <c>0</c>).</summary>
            public const string Zero = "$socigy$number.zero";

            /// <summary>Numeric one (PostgreSQL: <c>1</c>).</summary>
            public const string One = "$socigy$number.one";
        }

        public static class Text
        {
            /// <summary>Empty string default (PostgreSQL: <c>''</c>).</summary>
            public const string Empty = "$socigy$text.empty";
        }
    }
}
