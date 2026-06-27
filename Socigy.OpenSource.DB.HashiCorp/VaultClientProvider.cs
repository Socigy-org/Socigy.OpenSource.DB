using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Socigy.OpenSource.DB.Core.Diagnostics;
using VaultSharp;

namespace Socigy.OpenSource.DB.HashiCorp
{
#nullable enable
    /// <summary>
    /// Owns the <see cref="IVaultClient"/> and keeps its auth token alive. VaultSharp logs in once and caches
    /// the token forever with no renewal, so without this the token eventually expires and every Vault call
    /// (key fetch, credential leasing) starts failing. Renewable/periodic tokens are kept alive via
    /// renew-self; when renewal is exhausted (max TTL) and AppRole credentials are configured, a fresh login
    /// obtains a new token and the client is swapped. A static, non-renewable token cannot be saved — that is
    /// logged loudly so the operator switches to a periodic/renewable token or AppRole.
    /// </summary>
    public class VaultClientProvider
    {
        private readonly VaultConnectionOptions _options;
        private readonly ILogger? _logger;
        private volatile IVaultClient _client;
        // Serializes renewal/relogin so a timer tick racing a manual renewal cannot both relogin and overwrite
        // _client (which wastes Vault logins and leaves a nondeterministic active token).
        private readonly SemaphoreSlim _renewLock = new SemaphoreSlim(1, 1);

        public VaultClientProvider(VaultConnectionOptions options, ILogger? logger = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;
            _client = VaultClientFactory.Create(options);

            if (Internal.VaultSecurity.IsInsecureRemote(options.Address))
                _logger?.LogWarning(
                    "Vault address '{Address}' uses plaintext HTTP to a non-loopback host; tokens, keys and " +
                    "leased credentials will be sent unencrypted. Use https://.", options.Address);
        }

        /// <summary>The current client. Always read through this so callers pick up a post-relogin swap.</summary>
        public IVaultClient Client => _client;

        /// <summary>True when we can obtain a brand-new token by logging in again (AppRole, not a static token).</summary>
        internal bool CanRelogin =>
            string.IsNullOrEmpty(_options.Token)
            && !string.IsNullOrEmpty(_options.AppRoleId)
            && !string.IsNullOrEmpty(_options.AppRoleSecretId);

        /// <summary>
        /// Renews the current token, or re-logs-in when renewal can no longer extend it. Returns the token's
        /// remaining lifetime in seconds (used to schedule the next renewal), or <see langword="null"/> if
        /// unknown.
        /// </summary>
        public async Task<double?> RenewOrReloginAsync(CancellationToken cancellationToken = default)
        {
            // Hold the renewal lock for the whole lookup/renew/relogin so concurrent callers run one at a time;
            // a waiter re-reads _client below and sees any relogin the previous holder performed.
            await _renewLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await RenewOrReloginCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _renewLock.Release();
            }
        }

        // The actual renewal body, run under _renewLock. Marked internal virtual so a test can prove the lock
        // serializes concurrent callers without standing up a real Vault.
        internal virtual async Task<double?> RenewOrReloginCoreAsync(CancellationToken cancellationToken)
        {
            // Trackable by admins via the "Socigy.OpenSource.DB" ActivitySource, like the other Vault ops.
            using var activity = SocigyDbInstrumentation.ActivitySource.StartActivity("vault.token.renew", ActivityKind.Client);
            var client = _client;
            try
            {
                var before = (await client.V1.Auth.Token.LookupSelfAsync().ConfigureAwait(false)).Data;
                activity?.SetTag("vault.token.renewable", before.Renewable);
                activity?.SetTag("vault.token.ttl_s", before.TimeToLive);

                if (before.Renewable)
                {
                    var renewed = await client.V1.Auth.Token.RenewSelfAsync().ConfigureAwait(false);
                    // If renew-self still extended the lifetime (true for renewable and periodic tokens until
                    // they hit max TTL), the current token is healthy — keep it.
                    if (renewed.LeaseDurationSeconds > before.TimeToLive)
                        return renewed.LeaseDurationSeconds;
                }

                // Not renewable, or renewal capped at max TTL → a fresh token is required.
                if (CanRelogin)
                    return await ReloginAsync().ConfigureAwait(false);

                _logger?.LogError(
                    "Vault token is non-renewable or has reached its max TTL, and no AppRole credentials are " +
                    "configured to obtain a new one. Vault access will FAIL in ~{Ttl}s. Use a periodic/renewable " +
                    "token or AppRole auth for long-running services.", before.TimeToLive);
                return before.TimeToLive;
            }
            catch (Exception ex) when (CanRelogin)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger?.LogWarning(ex, "Vault token lookup/renew failed; re-authenticating via AppRole.");
                return await ReloginAsync().ConfigureAwait(false);
            }
        }

        private async Task<double?> ReloginAsync()
        {
            using var activity = SocigyDbInstrumentation.ActivitySource.StartActivity("vault.token.relogin", ActivityKind.Client);
            var fresh = VaultClientFactory.Create(_options); // performs a fresh AppRole login on first use
            _client = fresh;                                  // atomic swap; readers pick it up next call
            var info = (await fresh.V1.Auth.Token.LookupSelfAsync().ConfigureAwait(false)).Data;
            activity?.SetTag("vault.token.ttl_s", info.TimeToLive);
            _logger?.LogInformation("Re-authenticated to Vault via AppRole; new token TTL {Ttl}s.", info.TimeToLive);
            return info.TimeToLive;
        }
    }
#nullable disable
}
