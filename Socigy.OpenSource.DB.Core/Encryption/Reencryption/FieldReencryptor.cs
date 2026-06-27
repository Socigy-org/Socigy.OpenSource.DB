using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Socigy.OpenSource.DB.Core.CommandBuilders;
using Socigy.OpenSource.DB.Core.Diagnostics;
using Socigy.OpenSource.DB.Core.Interfaces;

namespace Socigy.OpenSource.DB.Core.Encryption.Reencryption
{
#nullable enable
    /// <summary>
    /// Bulk admin utility that rewrites <c>[Encrypted]</c> column values to the current key so an old key
    /// version can be retired. Old rows stay readable without this (versioned encryptors resolve old keys), so
    /// this is purely proactive. Re-encryption happens at the byte level — the value is decrypted then
    /// re-encrypted (or rewrapped, for Transit EaaS) — so no CLR codec is involved and any column type works.
    /// <para>
    /// There is no global table registry, so register the tables to process explicitly. Statically-named tables
    /// use their declared name for both the SQL target and the encryption context. Dynamic / <c>[TableType]</c>
    /// tables use the runtime name for the SQL target but the type's <b>declared</b> name for the encryption
    /// context — because that is the associated data the generated write path binds (it uses the compile-time
    /// <c>TableName</c>, not the runtime name).
    /// </para>
    /// </summary>
    public sealed class FieldReencryptor
    {
        private sealed class Registration
        {
            public IDbTable Proto = null!;
            public string SqlTable = "";   // physical table to read/update
            public string AadTable = "";   // table name used in the table:column associated data
        }

        private readonly List<Registration> _registrations = new List<Registration>();

        /// <summary>Registers a statically-named generated table by type.</summary>
        public FieldReencryptor Add<T>() where T : IDbTable, new() => Add(new T());

        /// <summary>Registers a table from a prototype instance (the instance is only used to read metadata).</summary>
        public FieldReencryptor Add(IDbTable table)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            string name = table.GetTableName();
            _registrations.Add(new Registration { Proto = table, SqlTable = name, AadTable = name });
            return this;
        }

        /// <summary>
        /// Registers a dynamic / <c>[TableType]</c> entity bound to a runtime table <paramref name="tableName"/>.
        /// The runtime name targets the physical table; the entity's declared name is used for the encryption
        /// context (matching what the write path bound).
        /// </summary>
        public FieldReencryptor AddDynamic<T>(string tableName) where T : IDbTable, new() => AddDynamic(new T(), tableName);

        /// <summary>Registers a dynamic table from a prototype instance bound to a runtime table name.</summary>
        public FieldReencryptor AddDynamic(IDbTable table, string tableName)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (string.IsNullOrEmpty(tableName)) throw new ArgumentException("A runtime table name is required.", nameof(tableName));
            _registrations.Add(new Registration { Proto = table, SqlTable = tableName, AadTable = table.GetTableName() });
            return this;
        }

        /// <summary>Runs the re-encryption pass over every registered table against <paramref name="connection"/>.</summary>
        public async Task<ReencryptReport> RunAsync(DbConnection connection, ReencryptOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            options ??= new ReencryptOptions();
            if (options.BatchSize < 1) throw new ArgumentException("BatchSize must be at least 1.", nameof(options));

            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var logger = SocigyDbDiagnostics.GetLogger();
            logger?.LogInformation(
                "Field re-encryption starting over {TableCount} table(s) (batchSize={BatchSize}, dryRun={DryRun}, force={Force}).",
                _registrations.Count, options.BatchSize, options.DryRun, options.Force);

            var report = new ReencryptReport();
            foreach (var reg in _registrations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tableResult = await RunTableAsync(connection, reg, options, logger, cancellationToken).ConfigureAwait(false);
                report.Tables[reg.SqlTable] = tableResult;
                report.TotalRowsScanned += tableResult.RowsScanned;
                report.TotalCellsUpgraded += tableResult.CellsUpgraded;
            }

            logger?.LogInformation(
                "Field re-encryption {Outcome}: {Cells} cell(s) across {Rows} row(s) in {Tables} table(s).",
                options.DryRun ? "dry-run complete" : "complete", report.TotalCellsUpgraded, report.TotalRowsScanned, report.Tables.Count);
            return report;
        }

        private static async Task<ReencryptTableResult> RunTableAsync(
            DbConnection connection, Registration reg, ReencryptOptions options, ILogger? logger, CancellationToken cancellationToken)
        {
            var result = new ReencryptTableResult();

            // Encrypted (non-PK) columns and the primary key, by DB column name.
            var encrypted = new List<EncCol>();
            foreach (var kv in reg.Proto.GetColumns())
                if (kv.Value.IsEncrypted && !kv.Value.IsPrimaryKey)
                    encrypted.Add(new EncCol(kv.Key, kv.Value.EncryptionProfile, BuildAad(reg.AadTable, kv.Key)));

            if (encrypted.Count == 0)
            {
                logger?.LogDebug("Re-encryption skipping table '{Table}': no encrypted columns.", reg.SqlTable);
                return result; // nothing to do for this table
            }

            var pkColumns = new List<string>();
            foreach (var kv in reg.Proto.GetPrimaryColumns())
                pkColumns.Add(kv.Key);
            if (pkColumns.Count == 0)
                throw new InvalidOperationException(
                    $"Table '{reg.SqlTable}' has no primary key; bulk re-encryption requires one to page through rows deterministically.");

            using var activity = SocigyDbInstrumentation.ActivitySource.StartActivity("socigy.db.encryption.reencrypt", ActivityKind.Internal);
            activity?.SetTag("db.table", reg.SqlTable);

            object?[]? cursor = null; // last-seen PK tuple for keyset pagination
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = await ReadBatchAsync(connection, reg.SqlTable, pkColumns, encrypted, cursor, options.BatchSize, cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0) break;

                result.RowsScanned += batch.Count;

                // Compute upgrades for the batch, then apply them (optionally inside one transaction).
                DbTransaction? tx = options.DryRun ? null : connection.BeginTransaction();
                try
                {
                    foreach (var row in batch)
                    {
                        var updates = new List<(string Column, byte[] NewValue)>();
                        for (int i = 0; i < encrypted.Count; i++)
                        {
                            byte[]? current = row.EncValues[i];
                            if (current == null) continue;

                            var encryptor = SocigyFieldEncryption.Require(encrypted[i].Profile);
                            byte[]? upgraded = await TryUpgradeAsync(encryptor, current, encrypted[i].Aad, options.Force).ConfigureAwait(false);
                            if (upgraded != null && !BytesEqual(upgraded, current))
                                updates.Add((encrypted[i].Column, upgraded));
                        }

                        if (updates.Count == 0) continue;
                        result.CellsUpgraded += updates.Count;

                        if (!options.DryRun)
                            await ApplyUpdateAsync(connection, tx!, reg.SqlTable, pkColumns, row.PkValues, updates, cancellationToken).ConfigureAwait(false);
                    }

                    tx?.Commit();
                }
                catch
                {
                    // A broken connection / aborted transaction is the common reason the batch threw; rolling back
                    // such a transaction throws again and would mask the original failure. Swallow only the
                    // rollback error so the real cause propagates, matching the other transactional sites.
                    try { tx?.Rollback(); } catch { /* preserve the original exception */ }
                    throw;
                }
                finally
                {
                    tx?.Dispose();
                }

                options.OnProgress?.Invoke(reg.SqlTable, result.RowsScanned, result.CellsUpgraded);

                cursor = batch[batch.Count - 1].PkValues;
                if (batch.Count < options.BatchSize) break;
            }

            activity?.SetTag("db.reencrypt.rows_scanned", result.RowsScanned);
            activity?.SetTag("db.reencrypt.cells_upgraded", result.CellsUpgraded);
            logger?.LogInformation(
                "Re-encryption of table '{Table}' {Outcome}: {Cells} cell(s) across {Rows} row(s).",
                reg.SqlTable, options.DryRun ? "dry-run complete" : "complete", result.CellsUpgraded, result.RowsScanned);
            return result;
        }

        private static async Task<byte[]?> TryUpgradeAsync(IFieldEncryptor encryptor, byte[] current, byte[] aad, bool force)
        {
            if (encryptor is IReencryptableEncryptor reenc)
            {
                if (!force && !reenc.NeedsUpgrade(current)) return null;
                return await reenc.UpgradeToCurrentAsync(current, aad).ConfigureAwait(false);
            }

            // No version concept: only touch it when explicitly forced (re-encrypt via decrypt+encrypt).
            if (!force) return null;
            return encryptor.Encrypt(encryptor.Decrypt(current, aad), aad);
        }

        private static async Task<List<RowData>> ReadBatchAsync(
            DbConnection connection, string sqlTable, List<string> pkColumns, List<EncCol> encrypted,
            object?[]? cursor, int batchSize, CancellationToken cancellationToken)
        {
            using var command = connection.CreateCommand();
            var sb = new StringBuilder();
            sb.Append("SELECT ");
            for (int i = 0; i < pkColumns.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Quote(pkColumns[i]));
            }
            foreach (var e in encrypted)
                sb.Append(", ").Append(Quote(e.Column));
            sb.Append(" FROM ").Append(Quote(sqlTable));

            if (cursor != null)
            {
                // Keyset pagination via row-value comparison: (pk1, pk2, …) > (@c0, @c1, …).
                sb.Append(" WHERE (");
                for (int i = 0; i < pkColumns.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Quote(pkColumns[i]));
                }
                sb.Append(") > (");
                for (int i = 0; i < pkColumns.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    string p = "@c" + i;
                    sb.Append(p);
                    AddParameter(command, p, cursor[i]);
                }
                sb.Append(')');
            }

            sb.Append(" ORDER BY ");
            for (int i = 0; i < pkColumns.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Quote(pkColumns[i]));
            }
            sb.Append(" LIMIT ").Append(batchSize);
            command.CommandText = sb.ToString();

            var rows = new List<RowData>(batchSize);
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                int pkCount = pkColumns.Count;
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var pk = new object?[pkCount];
                    for (int i = 0; i < pkCount; i++)
                    {
                        // Keyset pagination compares (pk) > (@cursor); a NULL key makes that comparison
                        // UNKNOWN, which would silently drop the row from every later batch. Fail loud instead.
                        if (reader.IsDBNull(i))
                            throw new InvalidOperationException(
                                "Re-encryption uses keyset pagination over the primary key, which cannot handle NULL key values. " +
                                "Table '" + sqlTable + "' has a row with a NULL key column '" + pkColumns[i] +
                                "'. Ensure the identifying columns are NOT NULL.");
                        pk[i] = reader.GetValue(i);
                    }

                    var enc = new byte[encrypted.Count][];
                    for (int i = 0; i < encrypted.Count; i++)
                    {
                        int ordinal = pkCount + i;
                        enc[i] = reader.IsDBNull(ordinal) ? null! : reader.GetFieldValue<byte[]>(ordinal);
                    }
                    rows.Add(new RowData(pk, enc));
                }
            }
            return rows;
        }

        private static async Task ApplyUpdateAsync(
            DbConnection connection, DbTransaction tx, string sqlTable, List<string> pkColumns, object?[] pkValues,
            List<(string Column, byte[] NewValue)> updates, CancellationToken cancellationToken)
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;

            var sb = new StringBuilder();
            sb.Append("UPDATE ").Append(Quote(sqlTable)).Append(" SET ");
            for (int i = 0; i < updates.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                string p = "@v" + i;
                sb.Append(Quote(updates[i].Column)).Append(" = ").Append(p);
                AddParameter(command, p, updates[i].NewValue);
            }
            sb.Append(" WHERE ");
            for (int i = 0; i < pkColumns.Count; i++)
            {
                if (i > 0) sb.Append(" AND ");
                string p = "@k" + i;
                sb.Append(Quote(pkColumns[i])).Append(" = ").Append(p);
                AddParameter(command, p, pkValues[i]);
            }
            command.CommandText = sb.ToString();
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static byte[] BuildAad(string table, string column) => Encoding.UTF8.GetBytes(table + ":" + column);

        private static void AddParameter(DbCommand command, string name, object? value)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            command.Parameters.Add(p);
        }

        private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private readonly struct EncCol
        {
            public readonly string Column;
            public readonly string? Profile;
            public readonly byte[] Aad;
            public EncCol(string column, string? profile, byte[] aad) { Column = column; Profile = profile; Aad = aad; }
        }

        private readonly struct RowData
        {
            public readonly object?[] PkValues;
            public readonly byte[]?[] EncValues;
            public RowData(object?[] pk, byte[]?[] enc) { PkValues = pk; EncValues = enc; }
        }
    }
#nullable disable
}
