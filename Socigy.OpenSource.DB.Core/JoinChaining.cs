using Socigy.OpenSource.DB.Core.Diagnostics;
using System;
using System.Data.Common;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Socigy.OpenSource.DB.Core
{
#nullable enable
    /// <summary>Shared aggregate helper for the join-builder facades — runs the aggregate and converts the scalar (NULL over an empty set → <c>null</c>).</summary>
    internal static class JoinChaining
    {
        internal static async Task<TResult?> AggAsync<TResult>(
            JoinPlan plan, string func, LambdaExpression column,
            DbConnection? connection, DbTransaction? transaction, DbDiagnosticsContext? diagnostics, CancellationToken cancellationToken)
            where TResult : struct
        {
            var result = await plan.AggregateAsync(func, column, connection, transaction, diagnostics, cancellationToken).ConfigureAwait(false);
            if (result == null || result is DBNull) return null;
            // Route through ApplyDbValue (not raw Convert.ChangeType) so a DateTimeOffset result (Npgsql returns
            // timestamptz as a UTC DateTime) and widened unsigned types convert correctly, matching the
            // single-table aggregate path. Convert.ChangeType is still used internally for numerics.
            return global::Socigy.OpenSource.DB.Core.CommandBuilders.ColumnInfo.ApplyDbValue<TResult>(result);
        }
    }
#nullable disable
}
