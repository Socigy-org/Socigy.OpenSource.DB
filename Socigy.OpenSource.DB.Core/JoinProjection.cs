using Socigy.OpenSource.DB.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Socigy.OpenSource.DB.Core
{
#nullable enable
    /// <summary>
    /// A projected join query: streams the join's rows and maps each materialized tuple through a
    /// <see langword="client-side"/> compiled delegate into <typeparamref name="TResult"/>. Produced by a
    /// join builder's <c>Select(...)</c>. The projection runs in C# (AOT-safe, no runtime expression
    /// compilation); all columns are still fetched.
    /// </summary>
    public sealed class JoinProjection<TResult> : SqlCommandBuilder<JoinProjection<TResult>>
    {
        private readonly JoinPlan _plan;
        private readonly Func<IDbTable?[], TResult> _map;

        internal JoinProjection(JoinPlan plan, Func<IDbTable?[], TResult> map) { _plan = plan; _map = map; }

        public async IAsyncEnumerable<TResult> ExecuteAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var row in _plan.ExecuteRowsAsync(_Connection, _Transaction, _Diagnostics, cancellationToken).ConfigureAwait(false))
                yield return _map(row);
        }

        public async Task<List<TResult>> ToListAsync(CancellationToken cancellationToken = default)
        {
            var list = new List<TResult>();
            await foreach (var item in ExecuteAsync(cancellationToken).ConfigureAwait(false))
                list.Add(item);
            return list;
        }

        public async Task<TResult?> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
        {
            await foreach (var item in ExecuteAsync(cancellationToken).ConfigureAwait(false))
                return item;
            return default;
        }
    }
#nullable disable
}
