namespace Socigy.OpenSource.DB.Core.Context
{
#nullable enable
    /// <summary>How a database context acquires and holds its underlying <see cref="System.Data.Common.DbConnection"/>.</summary>
    public enum ConnectionLifetime
    {
        /// <summary>
        /// One connection is lazily opened on first use and reused for every operation in the scope,
        /// then disposed when the scope ends. With scoped DI this is one connection per request. Default.
        /// </summary>
        PerScope = 0,

        /// <summary>
        /// Each operation opens and disposes its own connection (relying on Npgsql pooling). Note: a
        /// transaction always pins a single connection regardless of this setting — a transaction cannot
        /// span connections.
        /// </summary>
        PerOperation = 1,
    }

    /// <summary>Options controlling a generated database context's connection behavior.</summary>
    public sealed class SocigyDbContextOptions
    {
        /// <summary>The connection-acquisition strategy. Default <see cref="ConnectionLifetime.PerScope"/>.</summary>
        public ConnectionLifetime ConnectionLifetime { get; set; } = ConnectionLifetime.PerScope;

        /// <summary>
        /// Optional connection-string sub-key passed to <see cref="IDbConnectionFactory.Create(string?)"/>
        /// (e.g. <c>"ReadOnly"</c>). <see langword="null"/> resolves to the default connection.
        /// </summary>
        public string? ConnectionKey { get; set; }
    }
#nullable disable
}
