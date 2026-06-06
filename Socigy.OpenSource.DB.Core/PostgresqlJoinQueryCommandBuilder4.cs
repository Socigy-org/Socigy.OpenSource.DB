using Socigy.OpenSource.DB.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Socigy.OpenSource.DB.Core
{
#nullable enable
    /// <summary>Four-table JOIN builder, yielding <c>(T1, T2, T3, T4)</c> tuples. This is the maximum arity; for more tables use a <c>.sql</c> procedure.</summary>
    public class PostgresqlJoinQueryCommandBuilder<T1, T2, T3, T4> : SqlCommandBuilder<PostgresqlJoinQueryCommandBuilder<T1, T2, T3, T4>>
        where T1 : class, IDbTable, new()
        where T2 : class, IDbTable, new()
        where T3 : class, IDbTable, new()
        where T4 : class, IDbTable, new()
    {
        private readonly JoinPlan _plan;
        internal PostgresqlJoinQueryCommandBuilder(JoinPlan plan) { _plan = plan; }

        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3, T4> Where(Expression<Func<T1, T2, T3, T4, bool>> where) { _plan.Where = where; return this; }
        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3, T4> OrderBy(Expression<Func<T1, T2, T3, T4, object?[]>> keys) { _plan.OrderBy = keys; _plan.OrderDesc = false; return this; }
        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3, T4> OrderByDesc(Expression<Func<T1, T2, T3, T4, object?[]>> keys) { _plan.OrderBy = keys; _plan.OrderDesc = true; return this; }
        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3, T4> Limit(int limit) { _plan.Limit = limit; return this; }
        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3, T4> Offset(int offset) { _plan.Offset = offset; return this; }

        public async IAsyncEnumerable<(T1?, T2?, T3?, T4?)> ExecuteAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var row in _plan.ExecuteRowsAsync(_Connection, _Transaction, _Diagnostics, cancellationToken).ConfigureAwait(false))
                yield return ((T1?)row[0], (T2?)row[1], (T3?)row[2], (T4?)row[3]);
        }

        public async Task<List<(T1?, T2?, T3?, T4?)>> ToListAsync(CancellationToken cancellationToken = default)
        {
            var list = new List<(T1?, T2?, T3?, T4?)>();
            await foreach (var item in ExecuteAsync(cancellationToken).ConfigureAwait(false)) list.Add(item);
            return list;
        }

        public async Task<(T1?, T2?, T3?, T4?)?> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
        {
            await foreach (var item in ExecuteAsync(cancellationToken).ConfigureAwait(false)) return item;
            return null;
        }

        public JoinProjection<TResult> Select<TResult>(Func<T1?, T2?, T3?, T4?, TResult> projector)
        {
            var p = new JoinProjection<TResult>(_plan, row => projector((T1?)row[0], (T2?)row[1], (T3?)row[2], (T4?)row[3]));
            if (_Transaction != null) p.WithTransaction(_Transaction); else if (_Connection != null) p.WithConnection(_Connection);
            p.WithDiagnostics(_Diagnostics);
            return p;
        }

        public Task<long> CountAsync(CancellationToken cancellationToken = default)
            => _plan.CountAsync(_Connection, _Transaction, _Diagnostics, cancellationToken);

        public Task<TResult?> SumAsync<TResult>(Expression<Func<T1, T2, T3, T4, object?>> column, CancellationToken cancellationToken = default) where TResult : struct
            => JoinChaining.AggAsync<TResult>(_plan, "SUM", column, _Connection, _Transaction, _Diagnostics, cancellationToken);
        public Task<TResult?> AvgAsync<TResult>(Expression<Func<T1, T2, T3, T4, object?>> column, CancellationToken cancellationToken = default) where TResult : struct
            => JoinChaining.AggAsync<TResult>(_plan, "AVG", column, _Connection, _Transaction, _Diagnostics, cancellationToken);
        public Task<TResult?> MinAsync<TResult>(Expression<Func<T1, T2, T3, T4, object?>> column, CancellationToken cancellationToken = default) where TResult : struct
            => JoinChaining.AggAsync<TResult>(_plan, "MIN", column, _Connection, _Transaction, _Diagnostics, cancellationToken);
        public Task<TResult?> MaxAsync<TResult>(Expression<Func<T1, T2, T3, T4, object?>> column, CancellationToken cancellationToken = default) where TResult : struct
            => JoinChaining.AggAsync<TResult>(_plan, "MAX", column, _Connection, _Transaction, _Diagnostics, cancellationToken);
    }
#nullable disable
}
