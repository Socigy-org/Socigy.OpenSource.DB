using System;
using System.Linq;
using System.Text;

namespace Socigy.OpenSource.DB.Checks
{
    /// <summary>
    /// An SQL expression fragment produced by the <see cref="DbCheck"/> builder.
    /// The <see cref="Sql"/> property holds the raw SQL string that will be embedded
    /// inside a CHECK constraint.
    /// </summary>
    public readonly struct DbCheckExpr
    {
        public string Sql { get; }

        public DbCheckExpr(string sql) => Sql = sql;

        public static implicit operator string(DbCheckExpr e) => e.Sql;
        public override string ToString() => Sql;
    }

    /// <summary>
    /// Implement this interface on a class and reference it from
    /// <c>[Check(typeof(MyCheck))]</c> to express type-safe, composable CHECK constraints
    /// using the <see cref="DbCheck"/> fluent builder.
    /// </summary>
    public interface IDbCheckExpression
    {
        /// <param name="columnName">
        /// The database column name when used on a property, <see langword="null"/> when used on a class.
        /// </param>
        DbCheckExpr Build(string? columnName);
    }

    /// <summary>
    /// Fluent builder for composing PostgreSQL CHECK constraint expressions.
    /// Use inside an <see cref="IDbCheckExpression.Build"/> implementation.
    /// </summary>
    public static class DbCheck
    {
        // ----------------------------------------------------------------
        // Column references
        // ----------------------------------------------------------------

        /// <summary>
        /// Creates a reference to a column identified by its C# property name.
        /// The name is automatically converted to snake_case and quoted.
        /// </summary>
        public static DbCheckExpr Value(string propertyName)
        {
            var col = ToSnakeCase(propertyName);
            return new DbCheckExpr($"\"{col}\"");
        }

        /// <summary>
        /// Creates a reference to a column by its exact database name (already in snake_case).
        /// </summary>
        public static DbCheckExpr Column(string dbColumnName)
            => new DbCheckExpr($"\"{dbColumnName}\"");

        // ----------------------------------------------------------------
        // String / pattern functions
        // ----------------------------------------------------------------

        /// <summary>
        /// <c>length(<paramref name="value"/>) <paramref name="op"/> <paramref name="length"/></c>
        /// </summary>
        public static DbCheckExpr Len(DbCheckExpr value, DbCheckExpr op, int length)
            => new DbCheckExpr($"length({value.Sql}) {op.Sql} {length}");

        /// <summary>
        /// <c><paramref name="value"/> LIKE '<paramref name="prefix"/>%'</c>
        /// </summary>
        public static DbCheckExpr StartsWith(DbCheckExpr value, string prefix)
            => new DbCheckExpr($"{value.Sql} LIKE '{EscapeSql(prefix)}%'");

        /// <summary>
        /// <c><paramref name="value"/> LIKE '%<paramref name="suffix"/>'</c>
        /// </summary>
        public static DbCheckExpr EndsWith(DbCheckExpr value, string suffix)
            => new DbCheckExpr($"{value.Sql} LIKE '%{EscapeSql(suffix)}'");

        /// <summary>
        /// <c><paramref name="value"/> LIKE '%<paramref name="substring"/>%'</c>
        /// </summary>
        public static DbCheckExpr Contains(DbCheckExpr value, string substring)
            => new DbCheckExpr($"{value.Sql} LIKE '%{EscapeSql(substring)}%'");

        /// <summary>
        /// PostgreSQL regex match: <c><paramref name="value"/> ~ '<paramref name="pattern"/>'</c>
        /// </summary>
        public static DbCheckExpr Regex(DbCheckExpr value, string pattern)
            => new DbCheckExpr($"{value.Sql} ~ '{EscapeSql(pattern)}'");

        // ----------------------------------------------------------------
        // Logical operators
        // ----------------------------------------------------------------

        /// <summary><c>NOT (<paramref name="expr"/>)</c></summary>
        public static DbCheckExpr Not(DbCheckExpr expr)
            => new DbCheckExpr($"NOT ({expr.Sql})");

        /// <summary><c>(<paramref name="left"/> AND <paramref name="right"/>)</c></summary>
        public static DbCheckExpr And(DbCheckExpr left, DbCheckExpr right)
            => new DbCheckExpr($"({left.Sql} AND {right.Sql})");

        /// <summary><c>(<paramref name="exprs"/> AND ...)</c> — variadic overload.</summary>
        public static DbCheckExpr And(params DbCheckExpr[] exprs)
            => new DbCheckExpr($"({string.Join(" AND ", exprs.Select(e => e.Sql))})");

        /// <summary><c>(<paramref name="left"/> OR <paramref name="right"/>)</c></summary>
        public static DbCheckExpr Or(DbCheckExpr left, DbCheckExpr right)
            => new DbCheckExpr($"({left.Sql} OR {right.Sql})");

        /// <summary><c>(<paramref name="exprs"/> OR ...)</c> — variadic overload.</summary>
        public static DbCheckExpr Or(params DbCheckExpr[] exprs)
            => new DbCheckExpr($"({string.Join(" OR ", exprs.Select(e => e.Sql))})");

        // ----------------------------------------------------------------
        // Comparison helpers
        // ----------------------------------------------------------------

        /// <summary><c><paramref name="left"/> = <paramref name="right"/></c></summary>
        public static DbCheckExpr Eq(DbCheckExpr left, DbCheckExpr right)
            => new DbCheckExpr($"{left.Sql} = {right.Sql}");

        /// <summary>Wraps a literal string value, quoting it for SQL.</summary>
        public static DbCheckExpr Literal(string value)
            => new DbCheckExpr($"'{EscapeSql(value)}'");

        /// <summary>Wraps a numeric literal.</summary>
        public static DbCheckExpr Literal(long value)
            => new DbCheckExpr(value.ToString());

        /// <summary>Wraps a numeric literal.</summary>
        public static DbCheckExpr Literal(double value)
            => new DbCheckExpr(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // ----------------------------------------------------------------
        // Comparison operator tokens — use with Len, Eq, etc.
        // ----------------------------------------------------------------

        public static class Operators
        {
            public static readonly DbCheckExpr LessThan        = new("<");
            public static readonly DbCheckExpr GreaterThan     = new(">");
            public static readonly DbCheckExpr LessOrEqual     = new("<=");
            public static readonly DbCheckExpr GreaterOrEqual  = new(">=");
            public static readonly DbCheckExpr Equal           = new("=");
            public static readonly DbCheckExpr NotEqual        = new("<>");
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private static string EscapeSql(string s) => s.Replace("'", "''");

        private static string ToSnakeCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsUpper(c) && i > 0)
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
