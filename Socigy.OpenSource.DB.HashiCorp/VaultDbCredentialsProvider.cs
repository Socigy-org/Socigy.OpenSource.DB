using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Socigy.OpenSource.DB.Core.Credentials;
using Socigy.OpenSource.DB.Core.Diagnostics;
using VaultSharp;

namespace Socigy.OpenSource.DB.HashiCorp
{
#nullable enable
    /// <summary>
    /// <see cref="IDbCredentialsProvider"/> backed by HashiCorp Vault's Database secrets engine. Each logical
    /// database name maps to a Vault role; <see cref="RefreshAsync"/> obtains short-lived credentials and
    /// composes a base connection string (cached), which <see cref="GetConnectionString"/> returns
    /// synchronously to the connection factory. A background service refreshes before the lease expires.
    ///
    /// <para>
    /// "Refresh" means <b>renew the existing lease</b> whenever Vault still allows it, and only lease afresh
    /// once the lease is non-renewable or has outlived
    /// <see cref="VaultCredentialsOptions.MaxLeaseLifetime"/>. That distinction matters more than it looks:
    /// a renewal keeps the same username and password, so the connection string is unchanged and Npgsql keeps
    /// using the pool it already has, whereas a re-lease mints a new PostgreSQL user and therefore a new pool.
    /// When a re-lease does happen, the superseded lease is revoked rather than left to age out, so the number
    /// of live dynamic roles per database stays at one instead of <c>ceil(TTL / RefreshInterval) + 1</c>.
    /// </para>
    /// </summary>
    public sealed class VaultDbCredentialsProvider : IDbCredentialsProvider, IDbConnectionSource, IDisposable
    {
        /// <summary>What is currently held for one database: the connection string plus the lease behind it.</summary>
        private sealed class LeasedCredentials
        {
            public string ConnectionString { get; set; } = "";
            public string? LeaseId { get; set; }
            public bool Renewable { get; set; }

            /// <summary>When the lease was first obtained, for the <c>MaxLeaseLifetime</c> bound.</summary>
            public DateTimeOffset IssuedAt { get; set; }

#if NET8_0_OR_GREATER
            /// <summary>
            /// The pool serving this credential. Owned here so that replacing the credential also disposes
            /// the pool behind it — Npgsql's own PoolManager keys by exact connection string and never evicts,
            /// so a rotating username otherwise strands a pool object per rotation, forever.
            /// </summary>
            public Npgsql.NpgsqlDataSource? DataSource { get; set; }
#endif
        }

        private readonly VaultClientProvider _clients;
        private readonly VaultCredentialsOptions _options;
        private readonly ILogger? _logger;
        private readonly ConcurrentDictionary<string, LeasedCredentials> _cache =
            new ConcurrentDictionary<string, LeasedCredentials>();

        // Smallest lease duration (seconds) observed in the most recent refresh round; drives the renewal
        // schedule so we renew before the shortest-lived credential expires. -1 until the first lease.
        private volatile int _minLeaseSeconds = -1;
        internal double? MinLeaseSeconds => _minLeaseSeconds > 0 ? _minLeaseSeconds : (double?)null;

        public VaultDbCredentialsProvider(VaultClientProvider clients, VaultCredentialsOptions options, ILogger? logger = null)
        {
            _clients = clients ?? throw new ArgumentNullException(nameof(clients));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;
        }

        public string? GetConnectionString(string database, string? connectionKey)
        {
            return _cache.TryGetValue(database, out var held) ? held.ConnectionString : null;
        }

        /// <inheritdoc/>
        public System.Data.Common.DbConnection? CreateConnection(string database, string? connectionKey)
        {
#if NET8_0_OR_GREATER
            // Serving from a data source this provider owns is what makes rotation non-leaking: LeaseAsync
            // disposes the previous one, so the pool goes with the credential rather than outliving it.
            return _cache.TryGetValue(database, out var held) ? held.DataSource?.CreateConnection() : null;
#else
            // netstandard2.0 has no DbDataSource. The connection factory falls back to composing a connection
            // from GetConnectionString, which is the pre-0.3.8 behaviour.
            return null;
#endif
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Builds the pool for a freshly leased credential. The database name is appended here because the
        /// data source is per-database, whereas the cached string is the shared base.
        /// </summary>
        private Npgsql.NpgsqlDataSource? BuildDataSource(string database, string baseConnectionString)
        {
            try
            {
                string full = baseConnectionString.TrimEnd().TrimEnd(';') + $";Database={database}";
                return Npgsql.NpgsqlDataSource.Create(full);
            }
            catch (Exception ex)
            {
                // A base connection string this provider cannot parse is still usable by the connection
                // factory's string path, so degrade rather than fail the lease.
                _logger?.LogWarning(ex,
                    "Could not build a pooled data source for '{Database}'; falling back to a plain connection string. " +
                    "Connection pools will not be reclaimed when credentials rotate.", database);
                return null;
            }
        }

        private void DisposeDataSource(string database, Npgsql.NpgsqlDataSource? dataSource)
        {
            if (dataSource == null) return;
            try { dataSource.Dispose(); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Disposing the superseded connection pool for '{Database}' failed.", database); }
        }
#endif

        public async ValueTask RefreshAsync(string database, string? connectionKey, CancellationToken cancellationToken = default)
        {
            if (!_options.DatabaseRoles.TryGetValue(database, out var role) || string.IsNullOrEmpty(role))
                throw new InvalidOperationException(
                    $"No Vault database role configured for database '{database}'. Add it to VaultCredentialsOptions.DatabaseRoles.");

            // Trackable by admins via the "Socigy.OpenSource.DB" ActivitySource + ILogger.
            using var activity = SocigyDbInstrumentation.ActivitySource.StartActivity("vault.credentials.lease", ActivityKind.Client);
            activity?.SetTag("db.name", database);
            activity?.SetTag("vault.database.role", role);

            _cache.TryGetValue(database, out var existing);

            if (await TryRenewAsync(database, existing, activity).ConfigureAwait(false))
                return;

            await LeaseAsync(database, role, replacing: existing, activity: activity).ConfigureAwait(false);
        }

        /// <summary>
        /// Extends the current lease in place. Returns false — without throwing — whenever renewal is not
        /// applicable or Vault declines it, so the caller falls through to a fresh lease. A renewal failure is
        /// an ordinary outcome (the role's <c>max_ttl</c> is reached eventually by definition), not an error.
        /// </summary>
        private async Task<bool> TryRenewAsync(string database, LeasedCredentials? held, Activity? activity)
        {
            if (!_options.RenewLeases || held?.LeaseId == null || !held.Renewable)
                return false;

            // Vault enforces the role's own max_ttl and would refuse anyway; checking here avoids a round-trip
            // whose only possible outcome is a failure we then have to recover from.
            if (DateTimeOffset.UtcNow - held.IssuedAt >= _options.MaxLeaseLifetime)
            {
                _logger?.LogDebug(
                    "Vault DB lease for '{Database}' has reached MaxLeaseLifetime ({Lifetime}); leasing a fresh credential.",
                    database, _options.MaxLeaseLifetime);
                return false;
            }

            try
            {
                int increment = (int)Math.Max(60, _options.RefreshInterval.TotalSeconds * 2);
                var renewed = await _clients.Client.V1.System
                    .RenewLeaseAsync(held.LeaseId, increment)
                    .ConfigureAwait(false);

                // Vault answers sys/leases/renew with the renewal on the ENVELOPE — lease_id, renewable and
                // lease_duration at the top level — and a null `data`. VaultSharp models the call as
                // Secret<RenewedLease> all the same, so `renewed.Data` is null here and reading through it
                // would NullReferenceException on every successful renewal. Read the envelope instead.
                held.Renewable = renewed.Renewable;
                RecordLeaseDuration(renewed.LeaseDurationSeconds);

                activity?.SetTag("vault.lease.renewed", true);
                activity?.SetTag("vault.lease.duration_s", renewed.LeaseDurationSeconds);
                _logger?.LogInformation(
                    "Renewed Vault DB lease for '{Database}' ({Lease}s remaining); credentials unchanged.",
                    database, renewed.LeaseDurationSeconds);
                return true;
            }
            catch (VaultSharp.Core.VaultApiException ex)
            {
                // Vault itself declined: expected at the role's max_ttl, and after a restart that lost the
                // lease. Falling back to a fresh lease is the correct handling, so this stays quiet.
                _logger?.LogDebug(ex,
                    "Vault declined to renew the DB lease for '{Database}'; leasing a fresh credential instead.",
                    database);
                return false;
            }
            catch (Exception ex)
            {
                // Anything else is unexpected, and falling back silently would restore precisely the
                // behaviour renewal exists to remove — a brand-new database role on every single refresh —
                // with nothing to show for it. Still degrade rather than break the application's
                // credentials, but say so loudly enough that a systematic failure is noticed.
                _logger?.LogWarning(ex,
                    "Unexpected failure renewing the Vault DB lease for '{Database}'; falling back to a fresh " +
                    "lease. If this repeats, every refresh is minting a new database role — please report it.",
                    database);
                return false;
            }
        }

        private async Task LeaseAsync(string database, string role, LeasedCredentials? replacing, Activity? activity)
        {
            try
            {
                var secret = await _clients.Client.V1.Secrets.Database
                    .GetCredentialsAsync(role, _options.DatabaseMountPoint)
                    .ConfigureAwait(false);

                string username = secret.Data.Username;
                string password = secret.Data.Password;

                // Build via DbConnectionStringBuilder so special characters in the leased password are escaped.
                string connectionString = Internal.VaultConnectionString.Compose(_options.BaseConnectionString, username, password);

                var replacement = new LeasedCredentials
                {
                    ConnectionString = connectionString,
                    LeaseId = secret.LeaseId,
                    Renewable = secret.Renewable,
                    IssuedAt = DateTimeOffset.UtcNow,
                };
#if NET8_0_OR_GREATER
                replacement.DataSource = BuildDataSource(database, connectionString);
#endif
                _cache[database] = replacement;

                RecordLeaseDuration(secret.LeaseDurationSeconds);

                activity?.SetTag("vault.lease.renewed", false);
                activity?.SetTag("vault.lease.duration_s", secret.LeaseDurationSeconds);
                _logger?.LogInformation(
                    "Leased Vault DB credentials for '{Database}' (role '{Role}', user '{User}', lease {Lease}s, renewable {Renewable})",
                    database, role, username, secret.LeaseDurationSeconds, secret.Renewable);

                // Only after the replacement is in place, so a failed revoke cannot leave the app without
                // working credentials. The old lease is already unreachable at this point.
                if (_options.RevokeOnRefresh && replacing?.LeaseId != null)
                    await RevokeAsync(database, replacing.LeaseId).ConfigureAwait(false);

#if NET8_0_OR_GREATER
                // Dispose the pool that served the superseded credential. Its connections are drained by
                // Npgsql; without this the pool object itself would live until process exit, which at N
                // databases and one rotation per MaxLeaseLifetime accumulates for the life of the service.
                DisposeDataSource(database, replacing?.DataSource);
#endif
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger?.LogError(ex, "Failed to lease Vault DB credentials for '{Database}' (role '{Role}')", database, role);
                throw;
            }
        }

        /// <summary>
        /// Revokes a lease, swallowing failures: the credential expires on its own at its TTL, so a failed
        /// revoke is untidy rather than dangerous and must never take down the caller.
        /// </summary>
        private async Task RevokeAsync(string database, string leaseId)
        {
            try
            {
                await _clients.Client.V1.System.RevokeLeaseAsync(leaseId).ConfigureAwait(false);
                _logger?.LogInformation("Revoked the superseded Vault DB lease for '{Database}'.", database);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Could not revoke the superseded Vault DB lease for '{Database}'. It will expire at its TTL.",
                    database);
            }
        }

        private void RecordLeaseDuration(int seconds)
        {
            if (seconds <= 0) return;
            int current = _minLeaseSeconds;
            if (current < 0 || seconds < current) _minLeaseSeconds = seconds;
        }

        /// <summary>Refreshes credentials for every configured database. Used by the background renewal service.</summary>
        public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
        {
            _minLeaseSeconds = -1; // recompute the shortest lease for this round
            foreach (var database in _options.DatabaseRoles.Keys)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await RefreshAsync(database, null, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Revokes every held lease, so a stopped process does not leave dynamic database roles alive for the
        /// remainder of their TTL. Best-effort and synchronous, because DI disposal is synchronous.
        /// </summary>
        public void Dispose()
        {
            foreach (var entry in _cache)
            {
                var leaseId = entry.Value.LeaseId;
                if (_options.RevokeOnShutdown && leaseId != null)
                {
                    try { RevokeAsync(entry.Key, leaseId).GetAwaiter().GetResult(); }
                    catch (Exception ex) { _logger?.LogDebug(ex, "Lease revoke on shutdown failed for '{Database}'.", entry.Key); }
                }

#if NET8_0_OR_GREATER
                DisposeDataSource(entry.Key, entry.Value.DataSource);
#endif
            }

            _cache.Clear();
        }

        internal VaultCredentialsOptions Options => _options;
    }
#nullable disable
}
