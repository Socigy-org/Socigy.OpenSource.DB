using System;
using System.Collections.Concurrent;
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
    /// database name maps to a Vault role; <see cref="RefreshAsync"/> leases short-lived credentials and
    /// composes a base connection string (cached), which <see cref="GetConnectionString"/> returns
    /// synchronously to the connection factory. A background service renews leases before they expire.
    /// </summary>
    public sealed class VaultDbCredentialsProvider : IDbCredentialsProvider
    {
        private readonly VaultClientProvider _clients;
        private readonly VaultCredentialsOptions _options;
        private readonly ILogger? _logger;
        private readonly ConcurrentDictionary<string, string> _cache = new ConcurrentDictionary<string, string>();

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
            return _cache.TryGetValue(database, out var cs) ? cs : null;
        }

        public async ValueTask RefreshAsync(string database, string? connectionKey, CancellationToken cancellationToken = default)
        {
            if (!_options.DatabaseRoles.TryGetValue(database, out var role) || string.IsNullOrEmpty(role))
                throw new InvalidOperationException(
                    $"No Vault database role configured for database '{database}'. Add it to VaultCredentialsOptions.DatabaseRoles.");

            // Trackable by admins via the "Socigy.OpenSource.DB" ActivitySource + ILogger.
            using var activity = SocigyDbInstrumentation.ActivitySource.StartActivity("vault.credentials.lease", ActivityKind.Client);
            activity?.SetTag("db.name", database);
            activity?.SetTag("vault.database.role", role);
            try
            {
                var secret = await _clients.Client.V1.Secrets.Database
                    .GetCredentialsAsync(role, _options.DatabaseMountPoint)
                    .ConfigureAwait(false);

                string username = secret.Data.Username;
                string password = secret.Data.Password;

                // Build via DbConnectionStringBuilder so special characters in the leased password are escaped.
                _cache[database] = Internal.VaultConnectionString.Compose(_options.BaseConnectionString, username, password);

                // Track the shortest lease seen this round so renewal can be scheduled off the real TTL.
                int lease = secret.LeaseDurationSeconds;
                if (lease > 0)
                {
                    int current = _minLeaseSeconds;
                    if (current < 0 || lease < current) _minLeaseSeconds = lease;
                }

                activity?.SetTag("vault.lease.duration_s", secret.LeaseDurationSeconds);
                _logger?.LogInformation(
                    "Leased Vault DB credentials for '{Database}' (role '{Role}', user '{User}', lease {Lease}s)",
                    database, role, username, secret.LeaseDurationSeconds);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger?.LogError(ex, "Failed to lease Vault DB credentials for '{Database}' (role '{Role}')", database, role);
                throw;
            }
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

        internal VaultCredentialsOptions Options => _options;
    }
#nullable disable
}
