using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Socigy.OpenSource.DB.Core.CommandBuilders;
using Socigy.OpenSource.DB.Core.Interfaces;

namespace Socigy.OpenSource.DB.Core.Bulk
{
#nullable enable
    /// <summary>
    /// High-throughput inserts via PostgreSQL binary COPY (<c>COPY … FROM STDIN (FORMAT BINARY)</c>). For
    /// large batches this is substantially faster than the parameterized multi-row INSERT path and is not
    /// bound by the 65535-parameter limit.
    /// <para>
    /// Trade-off: COPY cannot return database-generated values. Auto-increment / <c>DEFAULT</c> columns are
    /// still filled by the database, but those values are NOT written back to the in-memory instances (there
    /// is no <c>RETURNING</c> with COPY). When you need generated keys propagated, use the regular insert
    /// builder / <c>InsertMultipleAsync</c> instead.
    /// </para>
    /// </summary>
    public static class BulkCopy
    {
        /// <summary>
        /// Binary-COPYs <paramref name="rows"/> into their mapped table over <paramref name="connection"/>,
        /// returning the number of rows written. The connection is opened if necessary; its lifetime is the
        /// caller's. Pass <see cref="InsertFields.IncludeAutoIncrement"/> to also write auto-increment columns,
        /// or <see cref="InsertFields.ServerDefaults"/> (optionally with <paramref name="keep"/>) to omit
        /// <c>[Default]</c> columns so the server default applies.
        /// </summary>
        public static Task<ulong> InsertMultipleCopyAsync<T>(
            IEnumerable<T> rows,
            DbConnection connection,
            DbTransaction? transaction = null,
            InsertFields fields = InsertFields.Default,
            Expression<Func<T, object?[]>>? keep = null,
            CancellationToken cancellationToken = default)
            where T : class, IDbTable, IInsertPlanProvider
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            IReadOnlyList<T> list = rows as IReadOnlyList<T> ?? new List<T>(rows);
            if (list.Count == 0) return Task.FromResult(0UL);

            InsertColumnDescriptor[] cols = InsertFieldsResolver.Resolve<T>(
                list[0].GetInsertPlan(InsertFieldsResolver.IncludesAutoIncrement(fields)).Columns, fields, keep, list[0]);
            return CopyResolvedAsync(list, cols, connection, transaction, cancellationToken);
        }

        /// <summary>
        /// AOT-safe overload of <see cref="InsertMultipleCopyAsync{T}(IEnumerable{T}, DbConnection, DbTransaction, InsertFields, Expression{Func{T, object[]}}, CancellationToken)"/>
        /// naming the kept columns by string (property name or DB column name) instead of an <c>Expression</c>
        /// selector — the expression form forces <c>Expression.NewArrayInit</c> (<c>[RequiresDynamicCode]</c>).
        /// Supplying <paramref name="keepColumns"/> implies <c>ServerDefaults</c> for the unlisted <c>[Default]</c> columns.
        /// </summary>
        public static Task<ulong> InsertMultipleCopyAsync<T>(
            IEnumerable<T> rows,
            DbConnection connection,
            string[] keepColumns,
            DbTransaction? transaction = null,
            InsertFields fields = InsertFields.Default,
            CancellationToken cancellationToken = default)
            where T : class, IDbTable, IInsertPlanProvider
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            IReadOnlyList<T> list = rows as IReadOnlyList<T> ?? new List<T>(rows);
            if (list.Count == 0) return Task.FromResult(0UL);

            InsertColumnDescriptor[] cols = InsertFieldsResolver.Resolve(
                list[0].GetInsertPlan(InsertFieldsResolver.IncludesAutoIncrement(fields)).Columns, fields, keepColumns, list[0]);
            return CopyResolvedAsync(list, cols, connection, transaction, cancellationToken);
        }

        private static Task<ulong> CopyResolvedAsync<T>(IReadOnlyList<T> list, InsertColumnDescriptor[] cols,
            DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
            where T : class, IDbTable
        {
            if (cols.Length == 0) return Task.FromResult(0UL);

            string tableName = list[0].GetTableName();
            var boxed = new object[list.Count];
            for (int i = 0; i < list.Count; i++)
                boxed[i] = list[i];

            return CopyCoreAsync(connection, transaction, tableName, cols, boxed, cancellationToken);
        }

        /// <summary>
        /// Shared COPY core: builds the <c>COPY … FROM STDIN (FORMAT BINARY)</c> statement and the
        /// <see cref="CopyColumn"/> array from a precomputed insert plan, then runs the registered provider
        /// bridge. Used by both <see cref="InsertMultipleCopyAsync{T}"/> and <c>DynamicTable&lt;T&gt;</c>.
        /// </summary>
        internal static async Task<ulong> CopyCoreAsync(
            DbConnection connection,
            DbTransaction? transaction,
            string tableName,
            InsertColumnDescriptor[] cols,
            IReadOnlyList<object> rows,
            CancellationToken cancellationToken)
        {
            if (rows.Count == 0 || cols.Length == 0)
                return 0UL;

            var copyColumns = new CopyColumn[cols.Length];
            var command = new StringBuilder();
            command.Append("COPY ").Append(Quote(tableName)).Append(" (");
            for (int c = 0; c < cols.Length; c++)
            {
                // Insert-plan parameter names are the column name prefixed with '@'.
                string columnName = cols[c].ParameterName.Substring(1);
                string quoted = Quote(columnName);
                if (c > 0) command.Append(", ");
                command.Append(quoted);
                copyColumns[c] = new CopyColumn(quoted, cols[c].Type, cols[c].IsJson, cols[c].IsEncrypted, cols[c].GetValue);
            }
            command.Append(") FROM STDIN (FORMAT BINARY)");

            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            return await BulkCopySupport.CopyAsync(
                connection, transaction, command.ToString(), copyColumns, rows, cancellationToken).ConfigureAwait(false);
        }

        private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }
#nullable disable
}
