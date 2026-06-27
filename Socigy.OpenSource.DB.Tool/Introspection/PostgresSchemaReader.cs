using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;

namespace Socigy.OpenSource.DB.Tool.Introspection
{
    /// <summary>
    /// Reconstructs a <see cref="DbSchema"/> from a live PostgreSQL database by reading
    /// <c>information_schema</c> / <c>pg_catalog</c>. The result feeds the same downstream pipeline
    /// (<c>SchemaComparer</c>, <c>PostgreSqlGenerator</c>, and the scaffolding C# emitter) as the
    /// assembly-derived schema, so DB-first and code-first stay interchangeable.
    /// </summary>
    internal static class PostgresSchemaReader
    {
        public static async Task<DbSchema> ReadAsync(
            string connectionString,
            string schemaName = "public",
            CancellationToken cancellationToken = default)
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var tables = await ReadTablesAsync(conn, schemaName, cancellationToken).ConfigureAwait(false);
            var byName = tables.ToDictionary(t => t.Name, StringComparer.Ordinal);

            await ReadColumnsAsync(conn, schemaName, byName, cancellationToken).ConfigureAwait(false);
            await ApplyKeyColumnsAsync(conn, schemaName, byName, "PRIMARY KEY", cancellationToken).ConfigureAwait(false);
            await ReadUniqueConstraintsAsync(conn, schemaName, byName, cancellationToken).ConfigureAwait(false);
            await ReadForeignKeysAsync(conn, schemaName, byName, cancellationToken).ConfigureAwait(false);

            return new DbSchema { Id = Guid.NewGuid().ToString(), Tables = tables };
        }

        private static async Task<List<DbTable>> ReadTablesAsync(NpgsqlConnection conn, string schema, CancellationToken ct)
        {
            var list = new List<DbTable>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT table_name FROM information_schema.tables
                                WHERE table_schema = @s AND table_type = 'BASE TABLE'
                                ORDER BY table_name;";
            cmd.Parameters.AddWithValue("s", schema);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                string name = r.GetString(0);
                list.Add(new DbTable
                {
                    Name = name,
                    SourceName = Naming.ToPascalCase(name),
                    Columns = new List<DbColumn>(),
                    Constraints = new List<DbConstraint>()
                });
            }
            return list;
        }

        private static async Task ReadColumnsAsync(NpgsqlConnection conn, string schema, Dictionary<string, DbTable> tables, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT table_name, column_name, data_type, udt_name, is_nullable,
                                       column_default, character_maximum_length, is_identity,
                                       numeric_precision, numeric_scale
                                FROM information_schema.columns
                                WHERE table_schema = @s
                                ORDER BY table_name, ordinal_position;";
            cmd.Parameters.AddWithValue("s", schema);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                string tableName = r.GetString(0);
                if (!tables.TryGetValue(tableName, out var table))
                    continue;

                string columnName = r.GetString(1);
                string dataType = r.GetString(2);
                string udtName = r.IsDBNull(3) ? "" : r.GetString(3);
                bool nullable = r.GetString(4) == "YES";
                string? columnDefault = r.IsDBNull(5) ? null : r.GetString(5);
                int? maxLength = r.IsDBNull(6) ? (int?)null : r.GetInt32(6);
                bool isIdentity = !r.IsDBNull(7) && r.GetString(7) == "YES";
                int? numericPrecision = r.IsDBNull(8) ? (int?)null : r.GetInt32(8);
                int? numericScale = r.IsDBNull(9) ? (int?)null : r.GetInt32(9);

                bool isAutoIncrement = isIdentity
                    || (columnDefault != null && columnDefault.TrimStart().StartsWith("nextval(", StringComparison.OrdinalIgnoreCase));
                bool isJson = dataType.Equals("jsonb", StringComparison.OrdinalIgnoreCase)
                    || dataType.Equals("json", StringComparison.OrdinalIgnoreCase);

                var col = new DbColumn
                {
                    Name = columnName,
                    SourceName = Naming.ToPascalCase(columnName),
                    DotnetType = PostgresInverseTranslator.PgTypeToCSharp(dataType, udtName, maxLength),
                    // A JSON column canonicalizes to jsonb — the ORM stores both [RawJsonColumn]/[JsonColumn] as
                    // jsonb, so the analyzer reports "jsonb"; emitting the raw "json" here caused a spurious (and
                    // data-touching) ALTER ... TYPE jsonb on every scaffold->generate round-trip.
                    DatabaseType = isJson ? "jsonb" : BuildDatabaseType(dataType, maxLength, numericPrecision, numericScale),
                    // The analyzer never sets Nullable=false — a non-nullable column is Nullable==null. Storing
                    // `false` here made every NOT NULL column compare unequal (false != null) and emit a spurious
                    // SET NOT NULL on the first round-trip. Mirror the analyzer: true when nullable, null otherwise.
                    Nullable = nullable ? true : (bool?)null,
                    IsAutoIncrement = isAutoIncrement ? true : (bool?)null,
                    MaxLength = (maxLength.HasValue && IsVarchar(dataType)) ? maxLength : null,
                    IsJsonColumn = isJson ? true : (bool?)null,
                    DefaultValue = isAutoIncrement ? null : PostgresInverseTranslator.InverseDefault(columnDefault),
                };
                table.Columns.Add(col);
            }
        }

        private static async Task ApplyKeyColumnsAsync(NpgsqlConnection conn, string schema, Dictionary<string, DbTable> tables, string constraintType, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT tc.table_name, kcu.column_name
                                FROM information_schema.table_constraints tc
                                JOIN information_schema.key_column_usage kcu
                                  ON kcu.constraint_name = tc.constraint_name
                                 AND kcu.constraint_schema = tc.constraint_schema
                                WHERE tc.constraint_type = @t AND tc.table_schema = @s
                                ORDER BY tc.table_name, kcu.ordinal_position;";
            cmd.Parameters.AddWithValue("t", constraintType);
            cmd.Parameters.AddWithValue("s", schema);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            // Rows arrive ORDER BY table_name, ordinal_position, so a per-table running index captures the key's
            // column order. For a primary key this is recorded as PrimaryKeyOrder so a composite PK whose key order
            // differs from the column declaration order survives the scaffold→migrate round-trip.
            bool isPrimaryKey = string.Equals(constraintType, "PRIMARY KEY", StringComparison.OrdinalIgnoreCase);
            string? currentTable = null;
            int keyOrdinal = 0;
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                string tableName = r.GetString(0);
                if (!tables.TryGetValue(tableName, out var table)) continue;
                if (tableName != currentTable) { currentTable = tableName; keyOrdinal = 0; }
                var col = table.Columns.FirstOrDefault(c => c.Name == r.GetString(1));
                if (col != null)
                {
                    col.IsPrimaryKey = true;
                    if (isPrimaryKey) col.PrimaryKeyOrder = keyOrdinal;
                }
                keyOrdinal++;
            }
        }

        private static async Task ReadUniqueConstraintsAsync(NpgsqlConnection conn, string schema, Dictionary<string, DbTable> tables, CancellationToken ct)
        {
            // (table, constraint) -> ordered column property names
            var grouped = new Dictionary<(string Table, string Name), List<string>>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT tc.table_name, tc.constraint_name, kcu.column_name
                                    FROM information_schema.table_constraints tc
                                    JOIN information_schema.key_column_usage kcu
                                      ON kcu.constraint_name = tc.constraint_name
                                     AND kcu.constraint_schema = tc.constraint_schema
                                    WHERE tc.constraint_type = 'UNIQUE' AND tc.table_schema = @s
                                    ORDER BY tc.constraint_name, kcu.ordinal_position;";
                cmd.Parameters.AddWithValue("s", schema);
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var key = (r.GetString(0), r.GetString(1));
                    if (!grouped.TryGetValue(key, out var cols)) grouped[key] = cols = new List<string>();
                    cols.Add(r.GetString(2));
                }
            }

            foreach (var ((tableName, _), columns) in grouped)
            {
                if (!tables.TryGetValue(tableName, out var table)) continue;
                table.Constraints.Add(new DbConstraint
                {
                    Type = DbConstraint.Types.Unique,
                    TableName = tableName,
                    Columns = columns.Select(c => Naming.ToPascalCase(c)).ToList(),
                });
            }
        }

        private static async Task ReadForeignKeysAsync(NpgsqlConnection conn, string schema, Dictionary<string, DbTable> tables, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT src.relname AS src_table, tgt.relname AS tgt_table,
                                       con.confdeltype, con.confupdtype,
                                       (SELECT array_agg(a.attname ORDER BY u.ord)
                                          FROM unnest(con.conkey) WITH ORDINALITY AS u(attnum, ord)
                                          JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = u.attnum) AS src_cols,
                                       (SELECT array_agg(a.attname ORDER BY u.ord)
                                          FROM unnest(con.confkey) WITH ORDINALITY AS u(attnum, ord)
                                          JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = u.attnum) AS tgt_cols
                                FROM pg_constraint con
                                JOIN pg_class src ON src.oid = con.conrelid
                                JOIN pg_class tgt ON tgt.oid = con.confrelid
                                JOIN pg_namespace ns ON ns.oid = src.relnamespace
                                WHERE con.contype = 'f' AND ns.nspname = @s;";
            cmd.Parameters.AddWithValue("s", schema);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                string srcTable = r.GetString(0);
                string tgtTable = r.GetString(1);
                char onDelete = ReadPgChar(r, 2);
                char onUpdate = ReadPgChar(r, 3);
                var srcCols = r.IsDBNull(4) ? Array.Empty<string>() : (string[])r.GetValue(4);
                var tgtCols = r.IsDBNull(5) ? Array.Empty<string>() : (string[])r.GetValue(5);

                if (!tables.TryGetValue(srcTable, out var table)) continue;

                // The query filters only the source table's schema; the target may live in another schema and so
                // was never scaffolded. Emitting [ForeignKey(typeof(<Target>))] for a class that doesn't exist
                // produces uncompilable output, so skip (and warn) rather than reference a missing type.
                if (!tables.ContainsKey(tgtTable))
                {
                    Logger.Warning($"Skipping foreign key {srcTable} -> {tgtTable}: the target table is not in the scaffolded schema '{schema}' (cross-schema FKs are not scaffolded).");
                    continue;
                }

                table.Constraints.Add(new DbConstraint
                {
                    Type = DbConstraint.Types.ForeignKey,
                    TableName = srcTable,
                    Columns = srcCols.Select(Naming.ToPascalCase).ToList(),
                    TargetTable = Naming.ToPascalCase(tgtTable),
                    TargetColumns = tgtCols.Select(Naming.ToPascalCase).ToList(),
                    OnDelete = PostgresInverseTranslator.InverseForeignKeyAction(onDelete),
                    OnUpdate = PostgresInverseTranslator.InverseForeignKeyAction(onUpdate),
                });
            }
        }

        // pg_constraint.confdeltype/confupdtype are the internal "char" type; read defensively across the
        // char/string representations Npgsql may surface.
        private static char ReadPgChar(System.Data.Common.DbDataReader r, int ordinal)
        {
            object value = r.GetValue(ordinal);
            return value switch
            {
                char c => c,
                string s when s.Length > 0 => s[0],
                _ => 'a' // default: NO ACTION
            };
        }

        private static bool IsVarchar(string dataType)
            => dataType.Equals("character varying", StringComparison.OrdinalIgnoreCase)
            || dataType.Equals("varchar", StringComparison.OrdinalIgnoreCase);

        internal static string BuildDatabaseType(string dataType, int? maxLength, int? numericPrecision, int? numericScale)
        {
            if (IsVarchar(dataType) && maxLength.HasValue)
                return $"character varying({maxLength.Value})";
            // An UNBOUNDED varchar (no length) is equivalent to text, and a scaffolded `string` property regenerates
            // as "text"; returning the raw "character varying" produced a spurious (data-touching) ALTER ... TYPE text
            // on every round-trip. Map it to text so the round-trip is a no-op.
            if (IsVarchar(dataType) && !maxLength.HasValue)
                return "text";
            // Fixed-length character must carry its length so it round-trips against the forward map (CLR char ->
            // "character(1)"); a bare "character" would report a spurious Type change on every scaffold→generate.
            if ((dataType.Equals("character", StringComparison.OrdinalIgnoreCase)
                 || dataType.Equals("char", StringComparison.OrdinalIgnoreCase)) && maxLength.HasValue)
                return $"character({maxLength.Value})";
            // Preserve numeric(precision[,scale]) so a scaffolded decimal column round-trips faithfully; an
            // unconstrained numeric reports a NULL precision and stays plain "numeric".
            if ((dataType.Equals("numeric", StringComparison.OrdinalIgnoreCase)
                 || dataType.Equals("decimal", StringComparison.OrdinalIgnoreCase)) && numericPrecision.HasValue)
                return numericScale.GetValueOrDefault() > 0
                    ? $"numeric({numericPrecision.Value},{numericScale.Value})"
                    : $"numeric({numericPrecision.Value})";
            return dataType;
        }
    }
}
