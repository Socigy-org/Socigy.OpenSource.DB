using System.Threading;
using System.Threading.Tasks;

namespace Socigy.OpenSource.DB.Core.Credentials
{
#nullable enable
    /// <summary>
    /// Supplies (and can rotate) the credentials used to build a database connection string — for example
    /// dynamic, short-lived credentials leased from HashiCorp Vault's Database secrets engine.
    /// <para>
    /// <see cref="GetConnectionString"/> is called synchronously by <c>IDbConnectionFactory.Create(...)</c>
    /// (which has no async hook), so it must return a <b>cached</b> value with no I/O. Implementations fetch
    /// and refresh credentials out-of-band — at startup via <see cref="RefreshAsync"/> and on a renewal
    /// timer before the lease expires — and serve the latest cached connection string from
    /// <see cref="GetConnectionString"/>. When credentials rotate, returning the new string causes Npgsql
    /// to open a fresh pool; connections in the old pool drain and fail over naturally.
    /// </para>
    /// </summary>
    public interface IDbCredentialsProvider
    {
        /// <summary>
        /// Returns the current cached <b>base</b> connection string for <paramref name="database"/>
        /// (the connection factory appends <c>;Database=...</c>), or <see langword="null"/> to let the
        /// factory fall back to <c>IConfiguration</c>. Must not perform I/O.
        /// </summary>
        /// <param name="database">The logical database name (the factory's service key, e.g. "AuthDb").</param>
        /// <param name="connectionKey">Optional sub-key (e.g. "ReadOnly"); <see langword="null"/> for the default.</param>
        string? GetConnectionString(string database, string? connectionKey);

        /// <summary>
        /// Primes or refreshes the cached credentials for <paramref name="database"/>. Called once at
        /// startup (from <c>EnsureDbExists()</c>) and by the implementation's own lease-renewal logic.
        /// </summary>
        ValueTask RefreshAsync(string database, string? connectionKey, CancellationToken cancellationToken = default);
    }
#nullable disable
}
