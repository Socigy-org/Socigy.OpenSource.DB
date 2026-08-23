using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Socigy.OpenSource.DB.Core.CommandBuilders;
using Socigy.OpenSource.DB.Core.Diagnostics;
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

            var planColumns = list[0].GetInsertPlan(InsertFieldsResolver.IncludesAutoIncrement(fields)).Columns;
            if (InsertFieldsResolver.IsRowDependent(fields))
                return CopyByRowShapeAsync(list, planColumns,
                    keep == null ? null : InsertFieldsResolver.ExtractMemberNames(keep, list[0]),
                    connection, transaction, cancellationToken);

            InsertColumnDescriptor[] cols = InsertFieldsResolver.Resolve<T>(planColumns, fields, keep, list[0]);
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

            var planColumns = list[0].GetInsertPlan(InsertFieldsResolver.IncludesAutoIncrement(fields)).Columns;
            if (InsertFieldsResolver.IsRowDependent(fields))
                return CopyByRowShapeAsync(list, planColumns,
                    keepColumns == null ? null : InsertFieldsResolver.MapDbColumnNames(keepColumns, list[0]),
                    connection, transaction, cancellationToken);

            InsertColumnDescriptor[] cols = InsertFieldsResolver.Resolve(planColumns, fields, keepColumns, list[0]);
            return CopyResolvedAsync(list, cols, connection, transaction, cancellationToken);
        }

        /// <summary>
        /// The <see cref="InsertFields.ServerDefaultsWhenUnset"/> COPY path: which columns a row writes depends
        /// on that row's values, so rows are grouped by the set of columns they omit and one COPY runs per
        /// distinct shape.
        ///
        /// <para>
        /// A batch whose rows agree — the usual case, and the only one for a batch of freshly-constructed rows
        /// — produces a single group and costs exactly what <see cref="InsertFields.ServerDefaults"/> costs.
        /// A batch that disagrees costs one COPY per shape, which is why the split is logged: a silently
        /// fragmented batch reads as "COPY got slower" with nothing to point at.
        /// </para>
        /// </summary>
        private static async Task<ulong> CopyByRowShapeAsync<T>(
            IReadOnlyList<T> list,
            InsertColumnDescriptor[] planColumns,
            HashSet<string>? kept,
            DbConnection connection,
            DbTransaction? transaction,
            CancellationToken cancellationToken)
            where T : class, IDbTable
        {
            var groups = new Dictionary<string, (InsertColumnDescriptor[] Columns, List<object> Rows)>(StringComparer.Ordinal);

            foreach (var row in list)
            {
                var resolved = InsertFieldsResolver.ResolveForRow(planColumns, row, kept);
                string key = InsertFieldsResolver.RowShapeKey(resolved);

                if (!groups.TryGetValue(key, out var group))
                {
                    group = (resolved, new List<object>());
                    groups[key] = group;
                }
                group.Rows.Add(row);
            }

            if (groups.Count > 1)
                DbDiagnostics.LogBulkCopyFragmented(list[0].GetTableName(), list.Count, groups.Count,
                    DescribeShapeDifferences(planColumns, groups));

            string tableName = list[0].GetTableName();
            ulong total = 0;
            foreach (var group in groups.Values)
                total += await CopyCoreAsync(connection, transaction, tableName, group.Columns, group.Rows, cancellationToken)
                    .ConfigureAwait(false);

            return total;
        }

        /// <summary>The <c>[Default]</c> columns the rows disagreed about, so the log names the actual cause.</summary>
        private static string DescribeShapeDifferences(
            InsertColumnDescriptor[] planColumns,
            Dictionary<string, (InsertColumnDescriptor[] Columns, List<object> Rows)> groups)
        {
            var varying = new List<string>();
            foreach (var column in planColumns)
            {
                if (!column.HasDbDefault) continue;

                bool present = false, absent = false;
                foreach (var group in groups.Values)
                {
                    if (Array.IndexOf(group.Columns, column) >= 0) present = true;
                    else absent = true;
                }
                if (present && absent) varying.Add(column.ParameterName.Substring(1));
            }
            return varying.Count == 0 ? "(none identified)" : string.Join(", ", varying);
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
