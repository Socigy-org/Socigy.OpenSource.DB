using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Socigy.OpenSource.DB.Core.Diagnostics;

namespace Socigy.OpenSource.DB.Core.Migrations
{
#nullable enable
    /// <summary>
    /// Applies a single migration atomically. The schema change (UpSql/DownSql) and the version-table
    /// bookkeeping row are executed inside one transaction, so the database can never end up with the
    /// schema changed but unrecorded (which would re-apply the migration on the next run) or recorded but
    /// not changed. A failure anywhere rolls the whole step back.
    /// </summary>
    public static class MigrationExecutor
    {
        /// <param name="connection">An open connection. The caller owns its lifetime.</param>
        /// <param name="migrationSql">The migration's Up or Down SQL.</param>
        /// <param name="recordVersionAsync">
        /// Writes the version row. It receives the active transaction so the insert enlists in the same unit
        /// of work as the schema change. Both commit together or roll back together.
        /// </param>
        public static async Task ApplyAtomicAsync(
            DbConnection connection,
            string migrationSql,
            Func<DbTransaction, Task> recordVersionAsync,
            DbDiagnosticsContext? diagnostics = null,
            CancellationToken cancellationToken = default)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (recordVersionAsync == null) throw new ArgumentNullException(nameof(recordVersionAsync));

            // netstandard2.0 has no BeginTransactionAsync/CommitAsync; the synchronous control-flow calls
            // are cheap and the actual SQL still runs through async command execution.
            var transaction = connection.BeginTransaction();
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = migrationSql;
                    await DbDiagnostics.ExecuteNonQueryAsync(command, "MIGRATE",
                        ct => command.ExecuteNonQueryAsync(ct), cancellationToken, diagnostics).ConfigureAwait(false);
                }

                await recordVersionAsync(transaction).ConfigureAwait(false);
                transaction.Commit();
            }
            catch
            {
                try { transaction.Rollback(); } catch { /* connection may be broken; surface the original error */ }
                throw;
            }
            finally
            {
                transaction.Dispose();
            }
        }
    }
#nullable disable
}
