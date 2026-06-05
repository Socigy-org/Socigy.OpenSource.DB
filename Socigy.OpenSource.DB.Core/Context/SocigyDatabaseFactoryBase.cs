using System;
using System.Data.Common;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Socigy.OpenSource.DB.Core.Diagnostics;

namespace Socigy.OpenSource.DB.Core.Context
{
#nullable enable
    /// <summary>
    /// Shared implementation of <see cref="ISocigyDatabaseFactory{TDatabase}"/>. The generated per-database
    /// factory derives from this and only supplies <see cref="CreateContext"/> (which constructs the
    /// generated context bound to a scope). Connection acquisition, transaction begin/commit/rollback,
    /// scope reentrancy, and the parent diagnostics span all live here.
    /// </summary>
    /// <typeparam name="TDatabase">The generated context interface (e.g. <c>IAuthDb</c>).</typeparam>
    public abstract class SocigyDatabaseFactoryBase<TDatabase> : ISocigyDatabaseFactory<TDatabase>
    {
        private readonly IDbConnectionFactory _connections;
        private readonly SocigyDbContextOptions _options;
        private readonly DbDiagnosticsContext? _diagnostics;

        protected SocigyDatabaseFactoryBase(
            IDbConnectionFactory connections,
            SocigyDbContextOptions? options = null,
            DbDiagnosticsContext? diagnostics = null)
        {
            _connections = connections ?? throw new ArgumentNullException(nameof(connections));
            _options = options ?? new SocigyDbContextOptions();
            _diagnostics = diagnostics;
        }

        /// <summary>Constructs the generated context bound to <paramref name="scope"/>.</summary>
        protected abstract TDatabase CreateContext(SocigyDbScope scope);

        public Task ExecuteAsync(Func<TDatabase, Task> work, CancellationToken cancellationToken = default)
            => RunAsync(async db => { await work(db).ConfigureAwait(false); return true; }, transactional: false, cancellationToken);

        public Task<TResult> ExecuteAsync<TResult>(Func<TDatabase, Task<TResult>> work, CancellationToken cancellationToken = default)
            => RunAsync(work, transactional: false, cancellationToken);

        public Task ExecuteTransactionAsync(Func<TDatabase, Task> work, CancellationToken cancellationToken = default)
            => RunAsync(async db => { await work(db).ConfigureAwait(false); return true; }, transactional: true, cancellationToken);

        public Task<TResult> ExecuteTransactionAsync<TResult>(Func<TDatabase, Task<TResult>> work, CancellationToken cancellationToken = default)
            => RunAsync(work, transactional: true, cancellationToken);

        private async Task<TResult> RunAsync<TResult>(Func<TDatabase, Task<TResult>> work, bool transactional, CancellationToken cancellationToken)
        {
            // Reentrancy: join an ambient scope rather than opening a nested connection/transaction.
            // A nested transactional call therefore shares the outer transaction; only the outermost commits.
            SocigyDbScope? ambient = SocigyDbScope.Current;
            if (ambient != null)
                return await work(CreateContext(ambient)).ConfigureAwait(false);

            bool pin = transactional || _options.ConnectionLifetime == ConnectionLifetime.PerScope;
            var scope = new SocigyDbScope(_connections, _options, pin, _diagnostics);
            scope.Enter();

            Activity? parent = null;
            DbTransaction? transaction = null;
            try
            {
                if (transactional)
                {
                    DbConnection conn = await scope.GetPinnedConnectionAsync(cancellationToken).ConfigureAwait(false);
                    parent = SocigyDbInstrumentation.ActivitySource.StartActivity("TRANSACTION (postgresql)", ActivityKind.Client);
                    if (parent != null && parent.IsAllDataRequested)
                        parent.SetTag("db.system", "postgresql");

                    // netstandard2.0 DbConnection/DbTransaction expose only the synchronous transaction API;
                    // the connection open and command execution remain async.
                    transaction = conn.BeginTransaction();
                    scope.SetAmbientTransaction(transaction);
                }

                TResult result = await work(CreateContext(scope)).ConfigureAwait(false);

                // A still-active command/stream after the delegate completed means a database call inside it
                // was not awaited (e.g. `async ctx => ctx.Users.ForEachAsync(...)`). Surfacing this here turns
                // an opaque "a command is already in progress" from the commit into actionable guidance.
                if (scope.IsPinnedBusy)
                    throw new InvalidOperationException(
                        "A query or ForEachAsync stream was still active when the unit of work completed — a " +
                        "database call inside ExecuteAsync/ExecuteTransactionAsync was not awaited. Use " +
                        "`async ctx => await ctx.Users.ForEachAsync(...)` (await every database call in the delegate).");

                if (transaction != null)
                {
                    transaction.Commit();
                    parent?.SetTag("db.transaction.outcome", "commit");
                }

                return result;
            }
            catch (Exception ex)
            {
                if (transaction != null)
                {
                    try { transaction.Rollback(); } catch { /* preserve the original exception */ }
                    parent?.SetTag("db.transaction.outcome", "rollback");
                }
                parent?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
            finally
            {
                transaction?.Dispose();
                parent?.Dispose();
                scope.Exit();
                await scope.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
#nullable disable
}
