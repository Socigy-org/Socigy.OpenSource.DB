using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Socigy.OpenSource.DB.Core.Diagnostics;

namespace Socigy.OpenSource.DB.Core.Context
{
#nullable enable
    /// <summary>
    /// The unit-of-work state shared by a generated context and its table sets for the duration of one
    /// <c>ExecuteAsync</c>/<c>ExecuteTransactionAsync</c> call: the pinned connection (when applicable),
    /// the ambient transaction, the diagnostics carrier, and an AsyncLocal pointer used to detect and
    /// join nested scopes. Also enforces the single-active-command rule on the shared connection
    /// (PostgreSQL has no MARS).
    /// </summary>
    public sealed class SocigyDbScope
    {
        private static readonly AsyncLocal<SocigyDbScope?> _current = new AsyncLocal<SocigyDbScope?>();

        /// <summary>The scope flowing on the current async context, if any. Used for transaction reentrancy.</summary>
        public static SocigyDbScope? Current => _current.Value;

        private readonly IDbConnectionFactory _factory;
        private readonly bool _pin;
        private DbConnection? _pinned;
        private DbTransaction? _ambientTx;
        private SocigyDbScope? _previous;
        private int _pinnedBusy; // 0 = free, 1 = a command/stream is active on the shared connection

        internal SocigyDbScope(IDbConnectionFactory factory, SocigyDbContextOptions options, bool pin, DbDiagnosticsContext? diagnostics)
        {
            _factory = factory;
            Options = options;
            _pin = pin;
            Diagnostics = diagnostics;
        }

        /// <summary>The options governing connection lifetime for this scope.</summary>
        public SocigyDbContextOptions Options { get; }

        /// <summary>The diagnostics carrier (logger + options) flowed into command builders, or <see langword="null"/>.</summary>
        public DbDiagnosticsContext? Diagnostics { get; }

        /// <summary>The active transaction, or <see langword="null"/> outside a transactional scope.</summary>
        public DbTransaction? AmbientTransaction => _ambientTx;

        /// <summary>Whether a transaction is currently active.</summary>
        public bool HasAmbientTransaction => _ambientTx != null;

        internal void Enter()
        {
            _previous = _current.Value;
            _current.Value = this;
        }

        internal void Exit() => _current.Value = _previous;

        internal void SetAmbientTransaction(DbTransaction transaction)
        {
            _ambientTx = transaction;
            _pinned = transaction.Connection;
        }

        /// <summary>Returns the scope's single pinned connection, opening it lazily. Used for transactions and streaming.</summary>
        public async ValueTask<DbConnection> GetPinnedConnectionAsync(CancellationToken cancellationToken = default)
        {
            if (_pinned == null)
                _pinned = _factory.Create(Options.ConnectionKey);
            if (_pinned.State != ConnectionState.Open)
                await _pinned.OpenAsync(cancellationToken).ConfigureAwait(false);
            return _pinned;
        }

        /// <summary>
        /// Acquires a connection for a single terminal operation. When the scope pins a connection
        /// (transaction or <see cref="ConnectionLifetime.PerScope"/>) the shared connection is returned
        /// and a single-active-command slot is taken; otherwise a fresh connection is opened that the
        /// caller must release. Pair with <see cref="ReleaseAsync"/>.
        /// </summary>
        public async ValueTask<(DbConnection Connection, bool OwnedByOperation)> AcquireAsync(CancellationToken cancellationToken = default)
        {
            if (_pin || _ambientTx != null)
            {
                AcquirePinnedSlot();
                try
                {
                    DbConnection conn = await GetPinnedConnectionAsync(cancellationToken).ConfigureAwait(false);
                    return (conn, false);
                }
                catch
                {
                    ReleasePinnedSlot();
                    throw;
                }
            }

            DbConnection fresh = _factory.Create(Options.ConnectionKey);
            await fresh.OpenAsync(cancellationToken).ConfigureAwait(false);
            return (fresh, true);
        }

        /// <summary>Releases a connection obtained from <see cref="AcquireAsync"/>.</summary>
        public async ValueTask ReleaseAsync(DbConnection connection, bool ownedByOperation)
        {
            if (ownedByOperation)
                await DisposeConnectionAsync(connection).ConfigureAwait(false);
            else
                ReleasePinnedSlot();
        }

        /// <summary>Begins a streaming read on the pinned connection, taking the single-active-command slot.</summary>
        public async ValueTask<DbConnection> BeginStreamAsync(CancellationToken cancellationToken = default)
        {
            AcquirePinnedSlot();
            try
            {
                return await GetPinnedConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ReleasePinnedSlot();
                throw;
            }
        }

        /// <summary>Ends a streaming read started with <see cref="BeginStreamAsync"/>.</summary>
        public void EndStream() => ReleasePinnedSlot();

        /// <summary>
        /// True while a command or <c>ForEachAsync</c> stream still holds the single-active-command slot on
        /// the pinned connection. After a unit of work's delegate has been awaited this should be false;
        /// if it isn't, a database call inside the delegate was not awaited (a forgotten <c>await</c>).
        /// </summary>
        internal bool IsPinnedBusy => Volatile.Read(ref _pinnedBusy) != 0;

        private void AcquirePinnedSlot()
        {
            if (Interlocked.CompareExchange(ref _pinnedBusy, 1, 0) != 0)
                throw new InvalidOperationException(
                    "A command was issued on this database context while another command or a ForEachAsync " +
                    "stream is already active on the same connection. PostgreSQL connections do not support " +
                    "multiple active result sets. Buffer rows with ToListAsync, collect your changes and apply " +
                    "them after the stream completes, or run the work in a separate scope.");
        }

        private void ReleasePinnedSlot() => Interlocked.Exchange(ref _pinnedBusy, 0);

        internal async ValueTask DisposeAsync()
        {
            // The transaction (and thus its disposal) is owned by the factory; here we only dispose a
            // connection the scope itself opened. When a transaction was used, _pinned is the tx's
            // connection and disposing it after the tx is disposed is correct.
            if (_pinned != null)
                await DisposeConnectionAsync(_pinned).ConfigureAwait(false);
            _pinned = null;
        }

        internal static async ValueTask DisposeConnectionAsync(DbConnection connection)
        {
            // netstandard2.0 DbConnection has no DisposeAsync; the concrete connection implements
            // IAsyncDisposable at runtime.
            if (connection is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else
                connection.Dispose();
        }
    }
#nullable disable
}
