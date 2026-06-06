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
    /// <summary>Three-table JOIN builder, yielding <c>(T1, T2, T3)</c> tuples. Chain <see cref="Join{T4}"/> for a fourth table.</summary>
    public class PostgresqlJoinQueryCommandBuilder<T1, T2, T3> : SqlCommandBuilder<PostgresqlJoinQueryCommandBuilder<T1, T2, T3>>
        where T1 : class, IDbTable, new()
        where T2 : class, IDbTable, new()
        where T3 : class, IDbTable, new()
    {
        private readonly JoinPlan _plan;
        internal PostgresqlJoinQueryCommandBuilder(JoinPlan plan) { _plan = plan; }

        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3> Where(Expression<Func<T1, T2, T3, bool>> where) { _plan.Where = where; return this; }
        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3> OrderBy(Expression<Func<T1, T2, T3, object?[]>> keys) { _plan.OrderBy = keys; _plan.OrderDesc = false; return this; }
        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3> OrderByDesc(Expression<Func<T1, T2, T3, object?[]>> keys) { _plan.OrderBy = keys; _plan.OrderDesc = true; return this; }
        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3> Limit(int limit) { _plan.Limit = limit; return this; }
        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3> Offset(int offset) { _plan.Offset = offset; return this; }

        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3, T4> Join<T4>(Expression<Func<T1, T2, T3, T4, bool>> on) where T4 : class, IDbTable, new()
            => Chain<T4>(JoinType.Inner, on);
        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3, T4> LeftJoin<T4>(Expression<Func<T1, T2, T3, T4, bool>> on) where T4 : class, IDbTable, new()
            => Chain<T4>(JoinType.Left, on);
        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3, T4> RightJoin<T4>(Expression<Func<T1, T2, T3, T4, bool>> on) where T4 : class, IDbTable, new()
            => Chain<T4>(JoinType.Right, on);
        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3, T4> FullOuterJoin<T4>(Expression<Func<T1, T2, T3, T4, bool>> on) where T4 : class, IDbTable, new()
            => Chain<T4>(JoinType.Full, on);
        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3, T4> NaturalJoin<T4>() where T4 : class, IDbTable, new()
            => Chain<T4>(JoinType.Natural, null);
        public PostgresqlJoinQueryCommandBuilder<T1, T2, T3, T4> CrossJoin<T4>() where T4 : class, IDbTable, new()
            => Chain<T4>(JoinType.Cross, null);

        private PostgresqlJoinQueryCommandBuilder<T1, T2, T3, T4> Chain<T4>(JoinType type, LambdaExpression? on) where T4 : class, IDbTable, new()
        {
            var plan = _plan.Clone();
            plan.Steps.Add(new JoinPlan.JoinStep { Prototype = new T4(), Factory = () => new T4(), Alias = "a" + plan.Steps.Count, Type = type, On = on });
            var next = new PostgresqlJoinQueryCommandBuilder<T1, T2, T3, T4>(plan);
            if (_Transaction != null) next.WithTransaction(_Transaction); else if (_Connection != null) next.WithConnection(_Connection);
            next.WithDiagnostics(_Diagnostics);
            return next;
        }

        public async IAsyncEnumerable<(T1?, T2?, T3?)> ExecuteAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var row in _plan.ExecuteRowsAsync(_Connection, _Transaction, _Diagnostics, cancellationToken).ConfigureAwait(false))
                yield return ((T1?)row[0], (T2?)row[1], (T3?)row[2]);
        }

        public async Task<List<(T1?, T2?, T3?)>> ToListAsync(CancellationToken cancellationToken = default)
        {
            var list = new List<(T1?, T2?, T3?)>();
            await foreach (var item in ExecuteAsync(cancellationToken).ConfigureAwait(false)) list.Add(item);
            return list;
        }

        public async Task<(T1?, T2?, T3?)?> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
        {
            await foreach (var item in ExecuteAsync(cancellationToken).ConfigureAwait(false)) return item;
            return null;
        }

        public JoinProjection<TResult> Select<TResult>(Func<T1?, T2?, T3?, TResult> projector)
        {
            var p = new JoinProjection<TResult>(_plan, row => projector((T1?)row[0], (T2?)row[1], (T3?)row[2]));
            if (_Transaction != null) p.WithTransaction(_Transaction); else if (_Connection != null) p.WithConnection(_Connection);
            p.WithDiagnostics(_Diagnostics);
            return p;
        }

        public Task<long> CountAsync(CancellationToken cancellationToken = default)
            => _plan.CountAsync(_Connection, _Transaction, _Diagnostics, cancellationToken);

        public Task<TResult?> SumAsync<TResult>(Expression<Func<T1, T2, T3, object?>> column, CancellationToken cancellationToken = default) where TResult : struct
            => JoinChaining.AggAsync<TResult>(_plan, "SUM", column, _Connection, _Transaction, _Diagnostics, cancellationToken);
        public Task<TResult?> AvgAsync<TResult>(Expression<Func<T1, T2, T3, object?>> column, CancellationToken cancellationToken = default) where TResult : struct
            => JoinChaining.AggAsync<TResult>(_plan, "AVG", column, _Connection, _Transaction, _Diagnostics, cancellationToken);
        public Task<TResult?> MinAsync<TResult>(Expression<Func<T1, T2, T3, object?>> column, CancellationToken cancellationToken = default) where TResult : struct
            => JoinChaining.AggAsync<TResult>(_plan, "MIN", column, _Connection, _Transaction, _Diagnostics, cancellationToken);
        public Task<TResult?> MaxAsync<TResult>(Expression<Func<T1, T2, T3, object?>> column, CancellationToken cancellationToken = default) where TResult : struct
            => JoinChaining.AggAsync<TResult>(_plan, "MAX", column, _Connection, _Transaction, _Diagnostics, cancellationToken);
    }
#nullable disable
}
