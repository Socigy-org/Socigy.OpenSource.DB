using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Socigy.OpenSource.DB.Attributes;
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
            await ReadIndexesAsync(conn, schemaName, byName, cancellationToken).ConfigureAwait(false);

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

        /// <summary>
        /// Reads standalone indexes into <see cref="DbTable.Indexes"/>, mapping each access method back to a
        /// portable <c>DbIndexMethods</c> token so a scaffolded schema is as engine-neutral as a generated one.
        /// </summary>
        /// <remarks>
        /// Indexes that merely implement a PRIMARY KEY or UNIQUE constraint are skipped: they are already
        /// modelled as constraints, and reading them here too would emit both a constraint and an index for
        /// the same thing, then generate a migration that adds a duplicate index on every run.
        /// <para>
        /// Expression indexes (<c>ON t ((lower(email)))</c>) have no attribute form. They are skipped with a
        /// warning rather than half-read, so the gap is visible instead of silently producing an index over
        /// the wrong thing.
        /// </para>
        /// </remarks>
        private static async Task ReadIndexesAsync(NpgsqlConnection conn, string schema, Dictionary<string, DbTable> tables, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT t.relname        AS table_name,
                                       i.relname        AS index_name,
                                       am.amname        AS access_method,
                                       ix.indisunique   AS is_unique,
                                       pg_get_expr(ix.indpred, ix.indrelid) AS filter,
                                       ix.indnkeyatts   AS key_count,
                                       ix.indoption::int2[] AS options,
                                       ARRAY(
                                           SELECT pg_get_indexdef(ix.indexrelid, k + 1, true)
                                           FROM generate_subscripts(ix.indkey, 1) AS k
                                           ORDER BY k
                                       )                AS column_defs
                                FROM pg_index ix
                                JOIN pg_class i     ON i.oid = ix.indexrelid
                                JOIN pg_class t     ON t.oid = ix.indrelid
                                JOIN pg_namespace n ON n.oid = t.relnamespace
                                JOIN pg_am am       ON am.oid = i.relam
                                WHERE n.nspname = @s
                                  AND NOT ix.indisprimary
                                  AND NOT EXISTS (
                                      SELECT 1 FROM pg_constraint c WHERE c.conindid = ix.indexrelid
                                  )
                                ORDER BY t.relname, i.relname;";
            cmd.Parameters.AddWithValue("s", schema);

            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                string tableName = r.GetString(0);
                string indexName = r.GetString(1);
                if (!tables.TryGetValue(tableName, out var table)) continue;

                string accessMethod = r.GetString(2);
                bool isUnique = r.GetBoolean(3);
                string filter = r.IsDBNull(4) ? null : r.GetString(4);
                int keyCount = r.GetInt32(5);
                var options = r.IsDBNull(6) ? Array.Empty<short>() : (short[])r.GetValue(6);
                var columnDefs = (string[])r.GetValue(7);

                var index = new DbIndex
                {
                    Name = indexName,
                    TableName = tableName,
                    IsUnique = isUnique,
                    Where = filter,
                };

                var keyColumns = new List<string>();
                var descending = new List<string>();
                var nullsFirst = new List<string>();
                var nullsLast = new List<string>();
                var included = new List<string>();
                bool expression = false;

                for (int i = 0; i < columnDefs.Length; i++)
                {
                    var column = ParseIndexColumn(columnDefs[i]);
                    if (column == null) { expression = true; break; }

                    // Columns past indnkeyatts are the INCLUDE list, which carries no ordering.
                    if (i >= keyCount) { included.Add(column); continue; }

                    keyColumns.Add(column);

                    var (isDescending, nulls) = DecodeIndexOption(i < options.Length ? options[i] : (short)0);
                    if (isDescending) descending.Add(column);
                    if (nulls == DbIndexNulls.First) nullsFirst.Add(column);
                    else if (nulls == DbIndexNulls.Last) nullsLast.Add(column);
                }

                if (expression || keyColumns.Count == 0)
                {
                    Logger.Warning($"Index \"{indexName}\" on \"{tableName}\" indexes an expression, which [Index] " +
                                   "cannot express. It was left out of the scaffolded model; re-create it by hand or " +
                                   "the next generated migration will drop it.");
                    continue;
                }

                index.Columns = keyColumns.Select(Naming.ToPascalCase).ToList();
                if (included.Count > 0) index.IncludeColumns = included.Select(Naming.ToPascalCase).ToList();
                if (descending.Count > 0) index.DescendingColumns = descending.Select(Naming.ToPascalCase).ToList();
                if (nullsFirst.Count > 0) index.NullsFirstColumns = nullsFirst.Select(Naming.ToPascalCase).ToList();
                if (nullsLast.Count > 0) index.NullsLastColumns = nullsLast.Select(Naming.ToPascalCase).ToList();

                var method = FromAccessMethod(accessMethod);
                if (method != null) index.Method = method;
                else if (!string.Equals(accessMethod, "btree", StringComparison.OrdinalIgnoreCase))
                    // No portable intent covers this access method, so keep it verbatim rather than losing it.
                    index.RawMethod = accessMethod;

                table.Indexes ??= new List<DbIndex>();
                table.Indexes.Add(index);
            }
        }

        /// <summary>Maps a PostgreSQL access method to a portable intent token, or null when none fits.</summary>
        /// <remarks>
        /// <c>gin</c> backs both full-text and containment indexes; containment is the broader everyday use,
        /// so it is the one recovered. The two generate identical SQL, so the choice cannot round-trip wrong.
        /// </remarks>
        private static string FromAccessMethod(string accessMethod) => accessMethod?.ToLowerInvariant() switch
        {
            "hash" => DbIndexMethods.Hash,
            "gin"  => DbIndexMethods.Contains,
            "gist" => DbIndexMethods.Spatial,
            "brin" => DbIndexMethods.BlockRange,
            _      => null,
        };

        /// <summary>
        /// Pulls the column name out of one <c>pg_get_indexdef</c> column fragment. Returns null for anything
        /// that is not a plain column reference (an expression, which has no attribute representation).
        /// </summary>
        /// <remarks>
        /// Per-column <c>pg_get_indexdef</c> renders only the column itself, never its sort options; those
        /// live in <c>pg_index.indoption</c> and are decoded by <see cref="DecodeIndexOption"/>.
        /// </remarks>
        private static string ParseIndexColumn(string definition)
        {
            var name = (definition ?? "").Trim();
            if (name.Length == 0) return null;

            if (name.StartsWith("\"", StringComparison.Ordinal) && name.EndsWith("\"", StringComparison.Ordinal))
                name = name.Substring(1, name.Length - 2);

            // A parenthesised or function-call fragment is an expression, not a column.
            return name.Length == 0 || name.Contains("(") || name.Contains(")") ? null : name;
        }

        // pg_index.indoption bit flags, per PostgreSQL's catalog definition.
        private const short IndexOptionDescending = 0x0001;
        private const short IndexOptionNullsFirst = 0x0002;

        /// <summary>
        /// Decodes one <c>pg_index.indoption</c> entry into sort direction and NULL placement.
        /// </summary>
        /// <remarks>
        /// Only a NULL placement that differs from the direction's default is reported. PostgreSQL defaults to
        /// NULLS LAST for ascending and NULLS FIRST for descending, and does not print the clause in that case;
        /// recording it anyway would make the regenerated DDL differ from the definition it was read from for
        /// no reason.
        /// </remarks>
        private static (bool Descending, string Nulls) DecodeIndexOption(short option)
        {
            bool descending = (option & IndexOptionDescending) != 0;
            bool nullsFirst = (option & IndexOptionNullsFirst) != 0;

            if (descending) return (true, nullsFirst ? null : DbIndexNulls.Last);
            return (false, nullsFirst ? DbIndexNulls.First : null);
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
