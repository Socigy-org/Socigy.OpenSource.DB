using System;
using System.Threading;
using System.Threading.Tasks;

namespace Socigy.OpenSource.DB.Core.Context
{
#nullable enable
    /// <summary>
    /// The injectable entry point to a generated database context. Resolve
    /// <c>ISocigyDatabaseFactory&lt;I{Db}&gt;</c> from DI and run work inside a scoped unit of work — the
    /// connection (and optional transaction) is acquired from the registered <see cref="IDbConnectionFactory"/>
    /// and disposed automatically, so business code never touches a <see cref="System.Data.Common.DbConnection"/>.
    /// Because <typeparamref name="TDatabase"/> is an interface, services depending on this are fully mockable.
    /// </summary>
    /// <remarks>
    /// The scope pins a single connection with one active command at a time (PostgreSQL has no MARS), so the
    /// <c>work</c> delegate must issue its database operations <b>sequentially</b> (<c>await</c> each before the
    /// next). Running them in parallel within one scope, e.g. <c>await Task.WhenAll(db.Users.CountAsync(),
    /// db.Orders.CountAsync())</c>, throws "a command is already active" rather than corrupting the connection;
    /// use separate <see cref="ExecuteAsync(Func{TDatabase, Task}, CancellationToken)"/> calls for genuine
    /// concurrency (each gets its own connection).
    /// </remarks>
    /// <typeparam name="TDatabase">The generated context interface (e.g. <c>IAuthDb</c>).</typeparam>
    public interface ISocigyDatabaseFactory<TDatabase>
    {
        /// <summary>Runs <paramref name="work"/> in a non-transactional scope (suitable for reads).</summary>
        Task ExecuteAsync(Func<TDatabase, Task> work, CancellationToken cancellationToken = default);

        /// <summary>Runs <paramref name="work"/> in a non-transactional scope and returns its result.</summary>
        Task<TResult> ExecuteAsync<TResult>(Func<TDatabase, Task<TResult>> work, CancellationToken cancellationToken = default);

        /// <summary>
        /// Runs <paramref name="work"/> inside a transaction: commits when it returns, rolls back if it
        /// throws. Operations inside auto-enlist in the transaction. Nested calls join the ambient
        /// transaction rather than opening a new one (only the outermost commits).
        /// </summary>
        Task ExecuteTransactionAsync(Func<TDatabase, Task> work, CancellationToken cancellationToken = default);

        /// <summary>Transactional variant of <see cref="ExecuteAsync{TResult}"/> returning a result.</summary>
        Task<TResult> ExecuteTransactionAsync<TResult>(Func<TDatabase, Task<TResult>> work, CancellationToken cancellationToken = default);
    }
#nullable disable
}
