using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Socigy.OpenSource.DB.Core.CommandBuilders;
using Socigy.OpenSource.DB.Core.Context;
using Socigy.OpenSource.DB.Core.Delegates;
using Socigy.OpenSource.DB.Core.Diagnostics;
using Socigy.OpenSource.DB.Core.Interfaces;
using Socigy.OpenSource.DB.Core.Parsers;
using Socigy.OpenSource.DB.Core.Parsers.Postgresql;

namespace Socigy.OpenSource.DB.Core.Dynamic
{
#nullable enable
    /// <summary>
    /// Runtime handle over a <c>[TableType]</c> entity bound to a table name chosen at runtime. Provides the
    /// full typed API (query, aggregates, insert/update/delete, lifecycle) for tables whose names aren't
    /// known at build time, returning typed <typeparamref name="T"/> rows. Works both standalone (via
    /// <see cref="WithConnection"/>/<see cref="WithTransaction"/>) and inside a database context scope.
    /// Obtain one via <c>T.WithTableName(name)</c>, <c>T.MapTypeAsync(name, conn)</c>, or
    /// <c>db.DynamicTable&lt;T&gt;(name)</c>.
    /// </summary>
    public sealed class DynamicTable<T> where T : class, IDbTableType<T>, new()
    {
        // Cache of discovered extra (undeclared) column names per (table type, runtime table name), so the
        // information_schema round-trip in MapTypeAsync happens once.
        private static readonly ConcurrentDictionary<(Type, string), string[]> _mapCache =
            new ConcurrentDictionary<(Type, string), string[]>();

        private readonly string _tableName;
        private readonly T _prototype;                 // throwaway instance for the IDbTableType<T> hooks
        private readonly SocigyDbScope? _scope;

        private DbConnection? _conn;
        private DbTransaction? _tx;
        private DbDiagnosticsContext? _diag;

        private Expression<Func<T, bool>>? _where;
        private string? _orderBy;
        private int _limit = -1;
        private int _offset = -1;
        private List<string>? _customColumns;

        /// <summary>Creates a handle for the given runtime table name. Bind a connection before executing.</summary>
        public DynamicTable(string tableName)
        {
            _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
            _prototype = new T();
        }

        /// <summary>Creates a scope-bound handle (used by the generated context's <c>DynamicTable&lt;T&gt;</c>).</summary>
        public DynamicTable(string tableName, SocigyDbScope scope) : this(tableName)
        {
            _scope = scope;
            _diag = scope.Diagnostics;
            if (scope.HasAmbientTransaction)
                _tx = scope.AmbientTransaction;
        }

        // ── Fluent configuration ───────────────────────────────────────────────────
        public DynamicTable<T> WithConnection(DbConnection connection) { _conn = connection; return this; }
        public DynamicTable<T> WithTransaction(DbTransaction transaction) { _tx = transaction; _conn = transaction.Connection; return this; }
        public DynamicTable<T> WithDiagnostics(DbDiagnosticsContext? diagnostics) { _diag = diagnostics; return this; }
        public DynamicTable<T> Where(Expression<Func<T, bool>> predicate) { _where = predicate; return this; }
        public DynamicTable<T> Query(Expression<Func<T, bool>>? predicate = null) { if (predicate != null) _where = predicate; return this; }
        /// <summary>Raw <c>ORDER BY</c> clause body (e.g. <c>"\"at\" DESC"</c>) — accepts declared or custom columns.</summary>
        public DynamicTable<T> OrderBy(string rawOrderBySql) { _orderBy = rawOrderBySql; return this; }
        public DynamicTable<T> Limit(int limit) { _limit = limit; return this; }
        public DynamicTable<T> Offset(int offset) { _offset = offset; return this; }

        /// <summary>Captures the named extra (undeclared) columns into each materialized row (read via <see cref="IDbTableType{T}.TryGetCustomValue{TValue}"/>).</summary>
        public DynamicTable<T> WithCustomColumns(params string[] columnNames)
        {
            _customColumns ??= new List<string>();
            foreach (var c in columnNames)
                if (!string.IsNullOrEmpty(c) && !_customColumns.Contains(c))
                    _customColumns.Add(c);
            return this;
        }

        // ── Reads ──────────────────────────────────────────────────────────────────
        public async IAsyncEnumerable<T> ExecuteAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var lease = await LeaseAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var command = lease.Connection.CreateCommand();
                if (_tx != null) command.Transaction = _tx;
                command.CommandText = BuildSelect(NewParser(command), "*", includeOrderLimit: true);

                await using var instr = await DbDiagnostics.ExecuteReaderAsync(
                    command, "SELECT", ct => command.ExecuteReaderAsync(ct), cancellationToken, _diag).ConfigureAwait(false);
                var reader = instr.Reader;

                int[]? ordinals = null;
                int[]? customOrdinals = null;
                while (await instr.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (ordinals == null)
                    {
                        ordinals = _prototype.ResolveOrdinals(reader);
                        customOrdinals = ResolveCustomOrdinals(reader);
                    }

                    T row = _prototype.MaterializeRow(reader, ordinals);
                    if (customOrdinals != null)
                        for (int i = 0; i < customOrdinals.Length; i++)
                            if (customOrdinals[i] >= 0)
                                row.SetCustomValue(_customColumns![i], reader.IsDBNull(customOrdinals[i]) ? null : reader.GetValue(customOrdinals[i]));

                    yield return row;
                }
            }
            finally
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async Task<List<T>> ToListAsync(CancellationToken cancellationToken = default)
        {
            var list = new List<T>();
            await foreach (var row in ExecuteAsync(cancellationToken).ConfigureAwait(false))
                list.Add(row);
            return list;
        }

        public async Task<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
        {
            int previous = _limit;
            _limit = 1;
            try
            {
                await foreach (var row in ExecuteAsync(cancellationToken).ConfigureAwait(false))
                    return row;
                return null;
            }
            finally { _limit = previous; }
        }

        public async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
            => await FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) != null;

        // ── Aggregates / scalars ────────────────────────────────────────────────────
        public async Task<long> CountAsync(CancellationToken cancellationToken = default)
        {
            var result = await RunScalarAsync("COUNT(*)", cancellationToken).ConfigureAwait(false);
            return result == null || result is DBNull ? 0L : Convert.ToInt64(result);
        }

        public Task<TResult?> SumAsync<TResult>(Expression<Func<T, object?>> selector, CancellationToken cancellationToken = default) where TResult : struct
            => AggregateAsync<TResult>("SUM", selector, cancellationToken);
        public Task<TResult?> AvgAsync<TResult>(Expression<Func<T, object?>> selector, CancellationToken cancellationToken = default) where TResult : struct
            => AggregateAsync<TResult>("AVG", selector, cancellationToken);
        public Task<TResult?> MinAsync<TResult>(Expression<Func<T, object?>> selector, CancellationToken cancellationToken = default) where TResult : struct
            => AggregateAsync<TResult>("MIN", selector, cancellationToken);
        public Task<TResult?> MaxAsync<TResult>(Expression<Func<T, object?>> selector, CancellationToken cancellationToken = default) where TResult : struct
            => AggregateAsync<TResult>("MAX", selector, cancellationToken);

        /// <summary>Reads a single column's value from the first matching row (<c>default</c> if none).</summary>
        public async Task<TResult?> ScalarAsync<TResult>(Expression<Func<T, object?>> selector, CancellationToken cancellationToken = default)
        {
            var result = await RunScalarAsync(Quote(AggColumn(selector)), cancellationToken).ConfigureAwait(false);
            if (result == null || result is DBNull) return default;
            var target = typeof(TResult);
            return (TResult)Convert.ChangeType(result, Nullable.GetUnderlyingType(target) ?? target);
        }

        private async Task<TResult?> AggregateAsync<TResult>(string func, Expression<Func<T, object?>> selector, CancellationToken cancellationToken) where TResult : struct
        {
            var result = await RunScalarAsync(func + "(" + Quote(AggColumn(selector)) + ")", cancellationToken).ConfigureAwait(false);
            if (result == null || result is DBNull) return null;
            var target = typeof(TResult);
            return (TResult)Convert.ChangeType(result, Nullable.GetUnderlyingType(target) ?? target);
        }

        private async Task<object?> RunScalarAsync(string projection, CancellationToken cancellationToken)
        {
            var lease = await LeaseAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var command = lease.Connection.CreateCommand();
                if (_tx != null) command.Transaction = _tx;
                command.CommandText = BuildSelect(NewParser(command), projection, includeOrderLimit: false);
                return await DbDiagnostics.ExecuteScalarAsync(
                    command, "SELECT", ct => command.ExecuteScalarAsync(ct), cancellationToken, _diag).ConfigureAwait(false);
            }
            finally { await lease.DisposeAsync().ConfigureAwait(false); }
        }

        // ── Writes ──────────────────────────────────────────────────────────────────
        public Task<int> InsertAsync(T row, bool includeAutoFields = false, CancellationToken cancellationToken = default)
            => InsertMultipleAsync(new[] { row }, includeAutoFields, cancellationToken);

        public async Task<int> InsertMultipleAsync(IEnumerable<T> rows, bool includeAutoFields = false, CancellationToken cancellationToken = default)
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            var list = rows as IList<T> ?? new List<T>(rows);
            if (list.Count == 0) return 0;

            InsertColumnDescriptor[] cols = _prototype.InsertColumns(includeAutoFields);
            if (cols.Length == 0) return 0;

            var lease = await LeaseAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                int maxRowsPerBatch = Math.Max(1, 65535 / cols.Length);
                int total = 0;
                for (int start = 0; start < list.Count; start += maxRowsPerBatch)
                {
                    int end = Math.Min(start + maxRowsPerBatch, list.Count);
                    using var command = lease.Connection.CreateCommand();
                    if (_tx != null) command.Transaction = _tx;

                    var sb = new System.Text.StringBuilder();
                    sb.Append("INSERT INTO ").Append(Quote(_tableName)).Append(" (");
                    for (int c = 0; c < cols.Length; c++)
                    {
                        if (c > 0) sb.Append(", ");
                        sb.Append(Quote(cols[c].ParameterName.Substring(1)));
                    }
                    sb.Append(") VALUES ");
                    for (int r = start; r < end; r++)
                    {
                        if (r > start) sb.Append(", ");
                        sb.Append('(');
                        for (int c = 0; c < cols.Length; c++)
                        {
                            if (c > 0) sb.Append(", ");
                            string paramName = "@p" + r + "_" + c;
                            sb.Append(cols[c].IsJson ? "CAST(" + paramName + " AS jsonb)" : paramName);
                            AddParameter(command, paramName, cols[c].GetValue(list[r]!), cols[c].Type);
                        }
                        sb.Append(')');
                    }

                    command.CommandText = sb.ToString();
                    total += await DbDiagnostics.ExecuteNonQueryAsync(
                        command, "INSERT", ct => command.ExecuteNonQueryAsync(ct), cancellationToken, _diag).ConfigureAwait(false);
                }
                return total;
            }
            finally { await lease.DisposeAsync().ConfigureAwait(false); }
        }

        /// <summary>Updates the columns of <paramref name="row"/> (primary-key and, by default, auto-increment columns excluded) for every row matching <paramref name="predicate"/>.</summary>
        public async Task<int> UpdateAsync(T row, Expression<Func<T, bool>> predicate, bool includeAutoFields = false, CancellationToken cancellationToken = default)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            var lease = await LeaseAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var command = lease.Connection.CreateCommand();
                if (_tx != null) command.Transaction = _tx;

                var columns = ((IDbTable)row).GetColumns();
                var sb = new System.Text.StringBuilder();
                sb.Append("UPDATE ").Append(Quote(_tableName)).Append(" SET ");
                int i = 0;
                foreach (var kv in columns)
                {
                    ColumnInfo info = kv.Value;
                    if (info.IsPrimaryKey) continue;
                    if (info.IsAutoIncrement && !includeAutoFields) continue;

                    if (i > 0) sb.Append(", ");
                    string paramName = "@s" + i;
                    sb.Append(Quote(kv.Key)).Append(" = ").Append(info.IsJson ? "CAST(" + paramName + " AS jsonb)" : paramName);
                    AddParameter(command, paramName, info.Value, info.Type);
                    i++;
                }
                if (i == 0) return 0;

                sb.Append(NewParser(command).ProcessWhere(predicate));
                command.CommandText = sb.ToString();
                return await DbDiagnostics.ExecuteNonQueryAsync(
                    command, "UPDATE", ct => command.ExecuteNonQueryAsync(ct), cancellationToken, _diag).ConfigureAwait(false);
            }
            finally { await lease.DisposeAsync().ConfigureAwait(false); }
        }

        public async Task<int> DeleteAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            var lease = await LeaseAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var command = lease.Connection.CreateCommand();
                if (_tx != null) command.Transaction = _tx;
                command.CommandText = "DELETE FROM " + Quote(_tableName) + NewParser(command).ProcessWhere(predicate);
                return await DbDiagnostics.ExecuteNonQueryAsync(
                    command, "DELETE", ct => command.ExecuteNonQueryAsync(ct), cancellationToken, _diag).ConfigureAwait(false);
            }
            finally { await lease.DisposeAsync().ConfigureAwait(false); }
        }

        // ── Auto-mapping (discover undeclared columns once, cached) ──────────────────
        public async Task<DynamicTable<T>> MapTypeAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            var key = (typeof(T), _tableName);
            if (!force && _mapCache.TryGetValue(key, out var cached))
            {
                _customColumns = new List<string>(cached);
                return this;
            }

            var declared = new HashSet<string>(((IDbTable)_prototype).GetColumns().Keys, StringComparer.OrdinalIgnoreCase);
            var extras = new List<string>();

            var lease = await LeaseAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var command = lease.Connection.CreateCommand();
                if (_tx != null) command.Transaction = _tx;
                command.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_name = @t ORDER BY ordinal_position";
                AddParameter(command, "@t", _tableName, typeof(string));

                await using var instr = await DbDiagnostics.ExecuteReaderAsync(
                    command, "SELECT", ct => command.ExecuteReaderAsync(ct), cancellationToken, _diag).ConfigureAwait(false);
                while (await instr.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    string name = instr.Reader.GetString(0);
                    if (!declared.Contains(name))
                        extras.Add(name);
                }
            }
            finally { await lease.DisposeAsync().ConfigureAwait(false); }

            var arr = extras.ToArray();
            _mapCache[key] = arr;
            _customColumns = new List<string>(arr);
            return this;
        }

        // ── Lifecycle (DDL) ─────────────────────────────────────────────────────────
        public async Task<int> InstantiateAsync(bool ifNotExists = true, CancellationToken cancellationToken = default)
        {
            var lease = await LeaseAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var command = lease.Connection.CreateCommand();
                if (_tx != null) command.Transaction = _tx;
                command.CommandText = _prototype.GetCreateTableSql(_tableName, ifNotExists);
                return await DbDiagnostics.ExecuteNonQueryAsync(
                    command, "CREATE", ct => command.ExecuteNonQueryAsync(ct), cancellationToken, _diag).ConfigureAwait(false);
            }
            finally { await lease.DisposeAsync().ConfigureAwait(false); }
        }

        public async Task<int> DeleteInstanceAsync(bool ifExists = true, CancellationToken cancellationToken = default)
        {
            var lease = await LeaseAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var command = lease.Connection.CreateCommand();
                if (_tx != null) command.Transaction = _tx;
                command.CommandText = "DROP TABLE " + (ifExists ? "IF EXISTS " : "") + Quote(_tableName);
                return await DbDiagnostics.ExecuteNonQueryAsync(
                    command, "DROP", ct => command.ExecuteNonQueryAsync(ct), cancellationToken, _diag).ConfigureAwait(false);
            }
            finally { await lease.DisposeAsync().ConfigureAwait(false); }
        }

        public async Task<bool> InstanceExistsAsync(CancellationToken cancellationToken = default)
        {
            var lease = await LeaseAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var command = lease.Connection.CreateCommand();
                if (_tx != null) command.Transaction = _tx;
                command.CommandText = "SELECT to_regclass(@t) IS NOT NULL";
                AddParameter(command, "@t", Quote(_tableName), typeof(string));
                var result = await DbDiagnostics.ExecuteScalarAsync(
                    command, "SELECT", ct => command.ExecuteScalarAsync(ct), cancellationToken, _diag).ConfigureAwait(false);
                return result is bool b && b;
            }
            finally { await lease.DisposeAsync().ConfigureAwait(false); }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────
        private SqlQueryBuilderExpressionParser<T> NewParser(DbCommand command)
            => new SqlQueryBuilderExpressionParser<T>(
                command,
                new GetColumnName(((IDbTable)_prototype).GetDbColumnName),
                (p, g, c) => new PostgresqlSelectVisitor(p, g, c),
                (p, g, c) => new PostgresqlWhereVisitor(p, g, c),
                (p, g, c, d) => new PostgresqlOrderByVisitor(p, g, c, d));

        private string BuildSelect(SqlQueryBuilderExpressionParser<T> parser, string projection, bool includeOrderLimit)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("SELECT ").Append(projection).Append(" FROM ").Append(Quote(_tableName));
            if (_where != null) sb.Append(parser.ProcessWhere(_where));
            if (includeOrderLimit)
            {
                if (!string.IsNullOrEmpty(_orderBy)) sb.Append(" ORDER BY ").Append(_orderBy);
                if (_limit >= 0) sb.Append(" LIMIT ").Append(_limit);
                if (_offset >= 0) sb.Append(" OFFSET ").Append(_offset);
            }
            return sb.ToString();
        }

        private int[]? ResolveCustomOrdinals(DbDataReader reader)
        {
            if (_customColumns == null || _customColumns.Count == 0) return null;
            var arr = new int[_customColumns.Count];
            for (int i = 0; i < arr.Length; i++)
            {
                try { arr[i] = reader.GetOrdinal(_customColumns[i]); }
                catch { arr[i] = -1; }
            }
            return arr;
        }

        private string AggColumn(LambdaExpression selector)
        {
            Expression body = selector.Body;
            if (body is UnaryExpression u && (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
                body = u.Operand;

            if (body is MemberExpression m && m.Expression is ParameterExpression)
            {
                var col = ((IDbTable)_prototype).GetDbColumnName(m.Member.Name);
                if (!string.IsNullOrEmpty(col)) return col!;
            }

            if (body is MethodCallExpression mc
                && mc.Method.DeclaringType == typeof(global::Socigy.OpenSource.DB.Core.SyntaxHelper.DB)
                && mc.Method.Name == "CustomField"
                && mc.Arguments.Count == 1
                && mc.Arguments[0] is ConstantExpression ce && ce.Value is string cs)
                return cs;

            throw new NotSupportedException("Aggregate/scalar selector must be a single column (x => x.Col) or DB.CustomField<T>(\"col\").");
        }

        // Engine-agnostic parameter binding: base DbParameter + value-type inference (JSON columns are cast
        // to jsonb in the SQL text; enums are reduced to their underlying value).
        private static void AddParameter(DbCommand command, string name, object? value, Type type)
        {
            var actual = Nullable.GetUnderlyingType(type) ?? type;
            if (actual.IsEnum && value != null && !(value is DBNull))
                value = Convert.ChangeType(value, Enum.GetUnderlyingType(actual));

            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            command.Parameters.Add(p);
        }

        private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

        private readonly struct Lease
        {
            private readonly SocigyDbScope? _scope;
            private readonly bool _owned;
            public DbConnection Connection { get; }

            public Lease(SocigyDbScope? scope, DbConnection connection, bool owned)
            {
                _scope = scope;
                Connection = connection;
                _owned = owned;
            }

            public async ValueTask DisposeAsync()
            {
                if (_scope != null)
                    await _scope.ReleaseAsync(Connection, _owned).ConfigureAwait(false);
            }
        }

        private async ValueTask<Lease> LeaseAsync(CancellationToken cancellationToken)
        {
            if (_scope != null)
            {
                var acquired = await _scope.AcquireAsync(cancellationToken).ConfigureAwait(false);
                return new Lease(_scope, acquired.Connection, acquired.OwnedByOperation);
            }

            if (_conn == null)
                throw new InvalidOperationException(
                    "No DbConnection was provided. Call WithConnection()/WithTransaction(), or obtain the handle from a context via db.DynamicTable<T>(name).");
            if (_conn.State != ConnectionState.Open)
                await _conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(null, _conn, false);
        }
    }
#nullable disable
}
