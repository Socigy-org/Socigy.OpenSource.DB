using System.Data.Common;

namespace Socigy.OpenSource.DB.Core.Credentials
{
#nullable enable
    /// <summary>
    /// Optional companion to <see cref="IDbCredentialsProvider"/> for providers that own their own connection
    /// pool instead of handing back a bare connection string.
    ///
    /// <para>
    /// Why it exists: the ADO.NET providers key their global pool by the exact connection string and never
    /// evict an entry. So a credentials provider whose username changes on rotation strands the previous pool
    /// object on every rotation — the connections inside drain via idle-lifetime settings, but the pool itself
    /// stays in the static registry for the life of the process. That is a slow leak in exactly the long-lived
    /// services that leased credentials exist for.
    /// </para>
    /// <para>
    /// A provider implementing this interface owns a data source per database and swaps and <b>disposes</b> it
    /// on rotation, so the old pool is released rather than abandoned. The connection factory prefers this
    /// over <see cref="IDbCredentialsProvider.GetConnectionString"/> when the registered provider offers it.
    /// </para>
    /// <para>
    /// The return type is <see cref="DbConnection"/> rather than a <c>DbDataSource</c> because this assembly
    /// targets netstandard2.0, which predates <c>DbDataSource</c>. The implementation holds the typed data
    /// source internally; only the connection crosses the boundary.
    /// </para>
    /// </summary>
    public interface IDbConnectionSource
    {
        /// <summary>
        /// Creates a connection to <paramref name="database"/> from the pool this source owns, or
        /// <see langword="null"/> to let the factory fall back to composing one from a connection string.
        /// Must not perform I/O: like <see cref="IDbCredentialsProvider.GetConnectionString"/>, this is called
        /// from a synchronous path.
        /// </summary>
        /// <param name="database">The logical database name (the factory's service key, e.g. "AuthDb").</param>
        /// <param name="connectionKey">Optional sub-key (e.g. "ReadOnly"); <see langword="null"/> for the default.</param>
        DbConnection? CreateConnection(string database, string? connectionKey);
    }
#nullable disable
}
