using Socigy.OpenSource.DB.Core.Enums;
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
    /// <summary>
    /// Builds and executes a two-table JOIN query, yielding <c>(T Left, TJoin Right)</c> tuples. Chain
    /// <see cref="Join{T3}"/> (etc.) for a third table, <c>OrderBy</c>/<c>Select</c>/aggregates for the rest.
    /// </summary>
    public class PostgresqlJoinQueryCommandBuilder<T, TJoin> : SqlCommandBuilder<PostgresqlJoinQueryCommandBuilder<T, TJoin>>
        where T : class, IDbTable, new()
        where TJoin : class, IDbTable, new()
    {
        private readonly JoinPlan _plan;

        public PostgresqlJoinQueryCommandBuilder(JoinType joinType, LambdaExpression? onExpression, LambdaExpression? drivingPredicate = null)
        {
            _plan = new JoinPlan { DrivingPredicate = drivingPredicate };
            _plan.Steps.Add(new JoinPlan.JoinStep { Prototype = new T(), Factory = () => new T(), Alias = "a0", Type = JoinType.None, On = null });
            _plan.Steps.Add(new JoinPlan.JoinStep { Prototype = new TJoin(), Factory = () => new TJoin(), Alias = "a1", Type = joinType, On = onExpression });
        }

        internal PostgresqlJoinQueryCommandBuilder(JoinPlan plan) { _plan = plan; }

        internal JoinPlan Plan => _plan;

        public PostgresqlJoinQueryCommandBuilder<T, TJoin> Where(Expression<Func<T, TJoin, bool>> where) { _plan.Where = where; return this; }
        public PostgresqlJoinQueryCommandBuilder<T, TJoin> OrderBy(Expression<Func<T, TJoin, object?[]>> keys) { _plan.OrderBy = keys; _plan.OrderDesc = false; return this; }
        public PostgresqlJoinQueryCommandBuilder<T, TJoin> OrderByDesc(Expression<Func<T, TJoin, object?[]>> keys) { _plan.OrderBy = keys; _plan.OrderDesc = true; return this; }
        public PostgresqlJoinQueryCommandBuilder<T, TJoin> Limit(int limit) { _plan.Limit = limit; return this; }
        public PostgresqlJoinQueryCommandBuilder<T, TJoin> Offset(int offset) { _plan.Offset = offset; return this; }

        // ── Chain a third table ─────────────────────────────────────────────────────
        public PostgresqlJoinQueryCommandBuilder<T, TJoin, T3> Join<T3>(Expression<Func<T, TJoin, T3, bool>> on) where T3 : class, IDbTable, new()
            => Chain<T3>(JoinType.Inner, on);
        public PostgresqlJoinQueryCommandBuilder<T, TJoin, T3> LeftJoin<T3>(Expression<Func<T, TJoin, T3, bool>> on) where T3 : class, IDbTable, new()
            => Chain<T3>(JoinType.Left, on);
        public PostgresqlJoinQueryCommandBuilder<T, TJoin, T3> RightJoin<T3>(Expression<Func<T, TJoin, T3, bool>> on) where T3 : class, IDbTable, new()
            => Chain<T3>(JoinType.Right, on);
        public PostgresqlJoinQueryCommandBuilder<T, TJoin, T3> FullOuterJoin<T3>(Expression<Func<T, TJoin, T3, bool>> on) where T3 : class, IDbTable, new()
            => Chain<T3>(JoinType.Full, on);
        public PostgresqlJoinQueryCommandBuilder<T, TJoin, T3> NaturalJoin<T3>() where T3 : class, IDbTable, new()
            => Chain<T3>(JoinType.Natural, null);
        public PostgresqlJoinQueryCommandBuilder<T, TJoin, T3> CrossJoin<T3>() where T3 : class, IDbTable, new()
            => Chain<T3>(JoinType.Cross, null);

        private PostgresqlJoinQueryCommandBuilder<T, TJoin, T3> Chain<T3>(JoinType type, LambdaExpression? on) where T3 : class, IDbTable, new()
        {
            var plan = _plan.Clone();
            plan.Steps.Add(new JoinPlan.JoinStep { Prototype = new T3(), Factory = () => new T3(), Alias = "a" + plan.Steps.Count, Type = type, On = on });
            var next = new PostgresqlJoinQueryCommandBuilder<T, TJoin, T3>(plan);
            if (_Transaction != null) next.WithTransaction(_Transaction); else if (_Connection != null) next.WithConnection(_Connection);
            next.WithDiagnostics(_Diagnostics);
            return next;
        }

        // ── Terminal: tuples / projection / aggregates ──────────────────────────────
        // Elements are nullable: an outer-join (Left/Right/Full) miss yields null for the unmatched side.
        public async IAsyncEnumerable<(T? Left, TJoin? Right)> ExecuteAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var row in _plan.ExecuteRowsAsync(_Connection, _Transaction, _Diagnostics, cancellationToken).ConfigureAwait(false))
                yield return ((T?)row[0], (TJoin?)row[1]);
        }

        public async Task<List<(T? Left, TJoin? Right)>> ToListAsync(CancellationToken cancellationToken = default)
        {
            var list = new List<(T?, TJoin?)>();
            await foreach (var item in ExecuteAsync(cancellationToken).ConfigureAwait(false)) list.Add(item);
            return list;
        }

        public async Task<(T? Left, TJoin? Right)?> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
        {
            await foreach (var item in ExecuteAsync(cancellationToken).ConfigureAwait(false)) return item;
            return null;
        }

        public JoinProjection<TResult> Select<TResult>(Func<T?, TJoin?, TResult> projector)
        {
            var p = new JoinProjection<TResult>(_plan, row => projector((T?)row[0], (TJoin?)row[1]));
            if (_Transaction != null) p.WithTransaction(_Transaction); else if (_Connection != null) p.WithConnection(_Connection);
            p.WithDiagnostics(_Diagnostics);
            return p;
        }

        public Task<long> CountAsync(CancellationToken cancellationToken = default)
            => _plan.CountAsync(_Connection, _Transaction, _Diagnostics, cancellationToken);

        public Task<TResult?> SumAsync<TResult>(Expression<Func<T, TJoin, object?>> column, CancellationToken cancellationToken = default) where TResult : struct
            => JoinChaining.AggAsync<TResult>(_plan, "SUM", column, _Connection, _Transaction, _Diagnostics, cancellationToken);
        public Task<TResult?> AvgAsync<TResult>(Expression<Func<T, TJoin, object?>> column, CancellationToken cancellationToken = default) where TResult : struct
            => JoinChaining.AggAsync<TResult>(_plan, "AVG", column, _Connection, _Transaction, _Diagnostics, cancellationToken);
        public Task<TResult?> MinAsync<TResult>(Expression<Func<T, TJoin, object?>> column, CancellationToken cancellationToken = default) where TResult : struct
            => JoinChaining.AggAsync<TResult>(_plan, "MIN", column, _Connection, _Transaction, _Diagnostics, cancellationToken);
        public Task<TResult?> MaxAsync<TResult>(Expression<Func<T, TJoin, object?>> column, CancellationToken cancellationToken = default) where TResult : struct
            => JoinChaining.AggAsync<TResult>(_plan, "MAX", column, _Connection, _Transaction, _Diagnostics, cancellationToken);
    }
#nullable disable
}
