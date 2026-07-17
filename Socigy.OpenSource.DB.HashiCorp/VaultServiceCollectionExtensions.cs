using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Socigy.OpenSource.DB.Core.Credentials;
using Socigy.OpenSource.DB.Core.Encryption;
using Socigy.OpenSource.DB.HashiCorp.Internal;

namespace Socigy.OpenSource.DB.HashiCorp
{
#nullable enable
    /// <summary>DI helpers that wire the HashiCorp Vault implementations into a Socigy.OpenSource.DB app.</summary>
    public static class VaultServiceCollectionExtensions
    {
        /// <summary>
        /// Registers a Vault-backed <see cref="IFieldEncryptor"/> for <c>[Encrypted]</c> columns. The key is
        /// read from Vault KV-v2 at host startup and installed as the ambient
        /// <see cref="SocigyFieldEncryption"/> encryptor. Background actions are logged and traced under the
        /// <c>Socigy.OpenSource.DB</c> ActivitySource.
        /// </summary>
        public static IServiceCollection AddSocigyVaultEncryption(this IServiceCollection services, Action<VaultEncryptionOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var options = new VaultEncryptionOptions();
            configure(options);

            // Shared client provider keeps the auth token alive (renew/relogin). If both Vault features are
            // registered they share one provider (TryAdd, first wins) — configure them with the same Vault
            // connection/auth settings; feature-specific paths/mounts stay on each feature's own options.
            services.TryAddSingleton(sp => new VaultClientProvider(options,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Client")));
            services.AddSingleton(sp => new VaultFieldEncryptor(
                sp.GetRequiredService<VaultClientProvider>(), options,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Encryption")));
            services.TryAddSingleton<IFieldEncryptor>(sp => sp.GetRequiredService<VaultFieldEncryptor>());
            AddEncryptionPriming(services, sp => sp.GetRequiredService<VaultFieldEncryptor>(), profile: null);
            services.AddHostedService(sp => new VaultAuthRenewalService(
                sp.GetRequiredService<VaultClientProvider>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<VaultAuthRenewalService>()));
            return services;
        }

        /// <summary>
        /// Registers the Vault Transit <b>data-key envelope</b> mode for <c>[Encrypted]</c> columns: a versioned
        /// keyring of Transit-wrapped DEKs is loaded at startup; per-field crypto stays local. Old rows remain
        /// decryptable across rotations. Installed as the default encryptor, or under
        /// <see cref="VaultTransitOptions.Profile"/> if set. Background rotation runs when
        /// <see cref="VaultTransitOptions.EnableBackgroundRotation"/> is enabled.
        /// </summary>
        public static IServiceCollection AddSocigyVaultEnvelopeEncryption(this IServiceCollection services, Action<VaultEnvelopeEncryptionOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var options = new VaultEnvelopeEncryptionOptions();
            configure(options);

            services.TryAddSingleton(sp => new VaultClientProvider(options,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Client")));
            services.AddSingleton(sp => new VaultEnvelopeEncryptor(
                sp.GetRequiredService<VaultClientProvider>(), options,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Encryption")));
            if (string.IsNullOrEmpty(options.Profile))
                services.TryAddSingleton<IFieldEncryptor>(sp => sp.GetRequiredService<VaultEnvelopeEncryptor>());

            AddEncryptionPriming(services, sp => sp.GetRequiredService<VaultEnvelopeEncryptor>(), options.Profile);
            services.AddHostedService(sp => new VaultAuthRenewalService(
                sp.GetRequiredService<VaultClientProvider>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<VaultAuthRenewalService>()));
            if (options.EnableBackgroundRotation)
                services.AddHostedService(sp => new VaultEncryptionRotationService(
                    sp.GetRequiredService<VaultEnvelopeEncryptor>(), options.RotationInterval,
                    sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Rotation")));
            return services;
        }

        /// <summary>
        /// Registers the Vault Transit <b>EaaS-direct</b> mode for <c>[Encrypted]</c> columns: each field is
        /// encrypted/decrypted by Vault per access (a round-trip per field). Intended for a few highly-sensitive
        /// columns via <see cref="VaultTransitOptions.Profile"/>. The Transit key must be created with
        /// <c>derived=true</c> so the table:column context binds.
        /// </summary>
        public static IServiceCollection AddSocigyVaultTransitEncryption(this IServiceCollection services, Action<VaultTransitEncryptionOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var options = new VaultTransitEncryptionOptions();
            configure(options);

            services.TryAddSingleton(sp => new VaultClientProvider(options,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Client")));
            services.AddSingleton(sp => new VaultTransitFieldEncryptor(
                sp.GetRequiredService<VaultClientProvider>(), options,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Encryption")));
            if (string.IsNullOrEmpty(options.Profile))
                services.TryAddSingleton<IFieldEncryptor>(sp => sp.GetRequiredService<VaultTransitFieldEncryptor>());

            AddEncryptionPriming(services, sp => sp.GetRequiredService<VaultTransitFieldEncryptor>(), options.Profile);
            services.AddHostedService(sp => new VaultAuthRenewalService(
                sp.GetRequiredService<VaultClientProvider>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<VaultAuthRenewalService>()));
            if (options.EnableBackgroundRotation)
                // Registered as a plain IHostedService (not AddHostedService) so an envelope rotator already
                // registered cannot de-duplicate this one away by implementation type.
                services.AddSingleton<IHostedService>(sp => new VaultEncryptionRotationService(
                    sp.GetRequiredService<VaultTransitFieldEncryptor>(), options.RotationInterval,
                    sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Rotation")));
            return services;
        }

        /// <summary>
        /// Registers one primer for this encryptor/profile, plus the single hosted service that primes them all.
        /// The primer is a plain enumerable singleton so every profile survives; the hosted service goes through
        /// <c>AddHostedService</c>, whose de-duplication by implementation type collapses the repeated
        /// registrations to exactly one collector.
        /// </summary>
        private static void AddEncryptionPriming(IServiceCollection services,
            Func<IServiceProvider, IVaultPrimableEncryptor> encryptor, string? profile)
        {
            services.AddSingleton<IVaultEncryptionPrimer>(sp => new VaultEncryptionPrimer(
                encryptor(sp), profile,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Encryption")));
            services.AddHostedService(sp => new VaultEncryptionPrimingService(
                sp.GetServices<IVaultEncryptionPrimer>()));
        }

        /// <summary>
        /// Registers a Vault-backed <see cref="IDbCredentialsProvider"/> that leases rotating DB credentials
        /// from Vault's Database secrets engine. The generated connection factory consumes it automatically.
        /// Background actions are logged and traced under the <c>Socigy.OpenSource.DB</c> ActivitySource.
        /// </summary>
        public static IServiceCollection AddSocigyVaultCredentials(this IServiceCollection services, Action<VaultCredentialsOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var options = new VaultCredentialsOptions();
            configure(options);

            services.TryAddSingleton(sp => new VaultClientProvider(options,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Client")));
            services.AddSingleton(sp => new VaultDbCredentialsProvider(
                sp.GetRequiredService<VaultClientProvider>(), options,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Credentials")));
            services.TryAddSingleton<IDbCredentialsProvider>(sp => sp.GetRequiredService<VaultDbCredentialsProvider>());
            services.AddHostedService<VaultCredentialsRenewalService>();
            services.AddHostedService(sp => new VaultAuthRenewalService(
                sp.GetRequiredService<VaultClientProvider>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<VaultAuthRenewalService>()));
            return services;
        }
    }

    /// <summary>Keeps the Vault auth token alive in the background (renew-self / AppRole relogin).</summary>
    internal sealed class VaultAuthRenewalService : IHostedService, IDisposable
    {
        private readonly VaultClientProvider _clients;
        private readonly ILogger? _logger;
        private Timer? _timer;
        private volatile bool _stopped;

        public VaultAuthRenewalService(VaultClientProvider clients, ILogger<VaultAuthRenewalService>? logger = null)
        {
            _clients = clients;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _timer = new Timer(_ => _ = TickAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            // First renewal shortly after startup; subsequent ones are scheduled off the real token TTL.
            try { _timer.Change(Internal.VaultRenewal.Floor, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
            return Task.CompletedTask;
        }

        private async Task TickAsync()
        {
            double? ttl = null;
            bool failed = false;
            try
            {
                ttl = await _clients.RenewOrReloginAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failed = true;
                _logger?.LogWarning(ex, "Vault token renewal failed; will retry shortly.");
            }

            if (_stopped) return;
            // On failure retry at the floor (~30s), not the 30-minute fallback: a short-lived token could
            // otherwise expire long before the next attempt.
            var delay = failed ? Internal.VaultRenewal.Floor : Internal.VaultRenewal.NextDelay(ttl, TimeSpan.FromMinutes(30));
            try { _timer?.Change(delay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _stopped = true;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            return Task.CompletedTask;
        }

        public void Dispose() => _timer?.Dispose();
    }

    /// <summary>
    /// Loads one encryptor's key material from Vault and installs it as the ambient encryptor — as the default
    /// when <see cref="Profile"/> is null/empty, or under that named profile otherwise. Registered per encryption
    /// helper call (as <see cref="IVaultEncryptionPrimer"/>, which DI never de-duplicates), so every profile is
    /// activated rather than only the first.
    /// </summary>
    internal sealed class VaultEncryptionPrimer : IVaultEncryptionPrimer
    {
        private readonly IVaultPrimableEncryptor _encryptor;
        private readonly ILogger? _logger;
        private readonly object _gate = new object();
        private Task? _priming;

        public VaultEncryptionPrimer(IVaultPrimableEncryptor encryptor, string? profile, ILogger? logger = null)
        {
            _encryptor = encryptor;
            Profile = profile;
            _logger = logger;
        }

        public string? Profile { get; }

        public Task PrimeAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                // Memoize the in-flight/completed task so an explicit UseSocigyVaultEncryption() and the later
                // hosted-service start share one Vault round-trip. A faulted/cancelled attempt is retried: a
                // transient Vault outage during activation must not permanently poison host startup.
                // (A bool flag would be wrong here — a second caller could return before Configure() had run.)
                if (_priming == null || _priming.IsFaulted || _priming.IsCanceled)
                    _priming = PrimeCoreAsync(cancellationToken);
                return _priming;
            }
        }

        private async Task PrimeCoreAsync(CancellationToken cancellationToken)
        {
            await _encryptor.RefreshAsync(cancellationToken).ConfigureAwait(false);
            SocigyFieldEncryption.Configure(Profile, _encryptor);
            _logger?.LogInformation("Vault field encryption primed and activated ({Profile}).",
                string.IsNullOrEmpty(Profile) ? "default profile" : "profile '" + Profile + "'");
        }
    }

    /// <summary>
    /// Primes every registered <see cref="IVaultEncryptionPrimer"/> at host start. One instance covers all of
    /// them, so <c>AddHostedService</c>'s de-duplication by implementation type is now correct rather than a
    /// silent dropper of profiles. Priming is idempotent, so calling
    /// <c>UseSocigyVaultEncryption()</c> before <c>Run()</c> makes this a no-op.
    /// </summary>
    internal sealed class VaultEncryptionPrimingService : IHostedService
    {
        private readonly IEnumerable<IVaultEncryptionPrimer> _primers;

        public VaultEncryptionPrimingService(IEnumerable<IVaultEncryptionPrimer> primers) => _primers = primers;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var primer in _primers)
                await primer.PrimeAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Rotates a Vault-backed encryptor's key on a background interval (one-shot timer re-armed each tick).
    /// The interval can exceed what <see cref="Timer"/> accepts as a dueTime (~49.7 days, and the default
    /// RotationInterval is 90), so every arm is clamped and a long interval is walked in clamped hops:
    /// a tick that fires before the interval has really elapsed just re-arms without rotating.
    /// </summary>
    internal sealed class VaultEncryptionRotationService : IHostedService, IDisposable
    {
        private readonly IVaultRotatableEncryptor _encryptor;
        private readonly TimeSpan _interval;
        private readonly ILogger? _logger;
        // Monotonic elapsed time since the last rotation attempt (Environment.TickCount64 is not in netstandard2.0).
        private readonly Stopwatch _sinceLastRotation = new Stopwatch();
        private Timer? _timer;
        private volatile bool _stopped;

        public VaultEncryptionRotationService(IVaultRotatableEncryptor encryptor, TimeSpan interval, ILogger? logger = null)
        {
            _encryptor = encryptor;
            _interval = interval;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _sinceLastRotation.Restart();
            // Arm through Arm() rather than the ctor: passing the raw interval here would throw for anything
            // over ~49.7 days, killing host startup (the default 90-day RotationInterval did exactly that).
            _timer = new Timer(async _ => await TickAsync().ConfigureAwait(false), null,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            Arm(_interval);
            return Task.CompletedTask;
        }

        private void Arm(TimeSpan remaining)
        {
            if (_stopped) return;
            var delay = VaultRenewal.ClampToTimer(remaining);
            try { _timer?.Change(delay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }

        private async Task TickAsync()
        {
            var arm = VaultRenewal.NextRotationArm(_interval - _sinceLastRotation.Elapsed, out bool rotateNow);
            if (!rotateNow)
            {
                // The interval is longer than one timer hop; keep waiting out the remainder.
                Arm(arm);
                return;
            }

            try
            {
                await _encryptor.RotateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Keep serving the current key until the next attempt.
                _logger?.LogWarning(ex, "Background encryption-key rotation failed; keeping current key.");
            }
            finally
            {
                // Reset after an attempt, success or not, so a failed rotation retries a full interval later.
                _sinceLastRotation.Restart();
                Arm(_interval);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _stopped = true;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            return Task.CompletedTask;
        }

        public void Dispose() => _timer?.Dispose();
    }

    /// <summary>Primes leased credentials at startup and renews them on a timer.</summary>
    internal sealed class VaultCredentialsRenewalService : IHostedService, IDisposable
    {
        private readonly VaultDbCredentialsProvider _provider;
        private readonly ILogger<VaultCredentialsRenewalService>? _logger;
        private Timer? _timer;
        private volatile bool _stopped;

        public VaultCredentialsRenewalService(VaultDbCredentialsProvider provider, ILogger<VaultCredentialsRenewalService>? logger = null)
        {
            _provider = provider;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _provider.RefreshAllAsync(cancellationToken).ConfigureAwait(false);
            // One-shot timer re-armed after each renewal, so the schedule tracks the actual lease TTL rather
            // than a fixed interval that could be longer than the lease (which would let credentials expire).
            _timer = new Timer(async _ => await RenewTickAsync().ConfigureAwait(false), null,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            ScheduleNext();
        }

        private void ScheduleNext(bool retrySoon = false)
        {
            if (_stopped) return;
            // After a failed renewal, retry at the floor (~30s) rather than 2/3 of a now-stale lease, which
            // could fall after the lease has actually expired.
            var delay = retrySoon
                ? Internal.VaultRenewal.Floor
                : Internal.VaultRenewal.NextDelay(_provider.MinLeaseSeconds, _provider.Options.RefreshInterval);
            _logger?.LogInformation("Next Vault DB credential renewal in {Delay}.", delay);
            try { _timer?.Change(delay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* stopping */ }
        }

        private async Task RenewTickAsync()
        {
            bool failed = false;
            try
            {
                _logger?.LogDebug("Renewing Vault DB credentials…");
                await _provider.RefreshAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Keep serving the last good credentials until the next attempt.
                failed = true;
                _logger?.LogWarning(ex, "Vault DB credential renewal failed; keeping previous credentials.");
            }
            finally
            {
                ScheduleNext(retrySoon: failed);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _stopped = true;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            return Task.CompletedTask;
        }

        public void Dispose() => _timer?.Dispose();
    }
#nullable disable
}
