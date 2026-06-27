using System;

namespace Socigy.OpenSource.DB.Core.CommandBuilders
{
#nullable enable
    public struct ColumnInfo
    {
        /// <summary>The CLR type of the column.</summary>
        public Type Type { get; set; }

        /// <summary>The current value of the column from the row instance.</summary>
        public object? Value { get; set; }

        /// <summary>Whether this column is part of the primary key.</summary>
        public bool IsPrimaryKey { get; set; }

        /// <summary>Whether this column is backed by a DB sequence and excluded from INSERT by default.</summary>
        public bool IsAutoIncrement { get; set; }

        /// <summary>Whether this column has a DB-level <c>DEFAULT</c> expression set via <c>[Default]</c>.</summary>
        public bool HasDbDefault { get; set; }

        /// <summary>
        /// True when this column is backed by a <c>jsonb</c> DB type.
        /// The insert/update builders will use <c>NpgsqlDbType.Jsonb</c> for this parameter.
        /// The value stored here is already serialized to a JSON string (or is the raw string for <c>[RawJsonColumn]</c>).
        /// </summary>
        public bool IsJson { get; set; }

        /// <summary>
        /// True when this column is <c>[Encrypted]</c> and stored as <c>bytea</c>.
        /// The insert/update builders use <c>NpgsqlDbType.Bytea</c> for this parameter; the value stored
        /// here is already the encrypted ciphertext (or <see langword="null"/>).
        /// </summary>
        public bool IsEncrypted { get; set; }

        /// <summary>
        /// For an <c>[Encrypted(Profile = "…")]</c> column, the encryptor profile this column routes to;
        /// <see langword="null"/>/empty means the default encryptor. Surfaced here so out-of-band tooling
        /// (e.g. the bulk re-encryption utility) can resolve the correct per-column encryptor at runtime.
        /// </summary>
        public string? EncryptionProfile { get; set; }

        /// <summary>
        /// Optional callback that writes a value read back from the database into the row instance.
        /// Used by <c>WithValuePropagation()</c> to fill auto-generated column values after INSERT.
        /// </summary>
        public Action<object?>? SetValue { get; set; }

        /// <summary>
        /// Converts a raw database value to <typeparamref name="T"/>, handling <c>null</c>,
        /// <c>DBNull</c>, enums, and common numeric/string conversions.
        /// </summary>
        public static T? ApplyDbValue<T>(object? dbValue)
        {
            if (dbValue is null || dbValue is DBNull) return default;
            if (dbValue is T direct) return direct;

            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            if (targetType.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(targetType);
                return (T)Enum.ToObject(targetType, Convert.ChangeType(dbValue, underlying));
            }

            // Npgsql returns a 'timestamp with time zone' as a UTC DateTime; map it onto a DateTimeOffset
            // target, which Convert.ChangeType cannot do (DateTimeOffset is not IConvertible).
            if (targetType == typeof(DateTimeOffset) && dbValue is DateTime dtv)
                return (T)(object)new DateTimeOffset(dtv.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dtv, DateTimeKind.Utc) : dtv);

            return (T)Convert.ChangeType(dbValue, targetType);
        }
    }
#nullable disable
}
