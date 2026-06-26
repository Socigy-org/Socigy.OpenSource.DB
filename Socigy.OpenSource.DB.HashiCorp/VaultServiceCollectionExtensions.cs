using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Socigy.OpenSource.DB.Core.Credentials;
using Socigy.OpenSource.DB.Core.Encryption;

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
            services.AddHostedService(sp => new VaultEncryptionPrimingService(
                sp.GetRequiredService<VaultFieldEncryptor>(), null,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Encryption")));
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

            services.AddHostedService(sp => new VaultEncryptionPrimingService(
                sp.GetRequiredService<VaultEnvelopeEncryptor>(), options.Profile,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Encryption")));
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

            services.AddHostedService(sp => new VaultEncryptionPrimingService(
                sp.GetRequiredService<VaultTransitFieldEncryptor>(), options.Profile,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Encryption")));
            services.AddHostedService(sp => new VaultAuthRenewalService(
                sp.GetRequiredService<VaultClientProvider>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<VaultAuthRenewalService>()));
            return services;
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
            try
            {
                ttl = await _clients.RenewOrReloginAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Vault token renewal failed; will retry.");
            }

            if (_stopped) return;
            var delay = Internal.VaultRenewal.NextDelay(ttl, TimeSpan.FromMinutes(30));
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
    /// Loads an encryptor's key material from Vault at startup and installs it as the ambient encryptor — as the
    /// default when <see cref="_profile"/> is null/empty, or under that named profile otherwise.
    /// </summary>
    internal sealed class VaultEncryptionPrimingService : IHostedService
    {
        private readonly IVaultPrimableEncryptor _encryptor;
        private readonly string? _profile;
        private readonly ILogger? _logger;

        public VaultEncryptionPrimingService(IVaultPrimableEncryptor encryptor, string? profile, ILogger? logger = null)
        {
            _encryptor = encryptor;
            _profile = profile;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _encryptor.RefreshAsync(cancellationToken).ConfigureAwait(false);
            SocigyFieldEncryption.Configure(_profile, _encryptor);
            _logger?.LogInformation("Vault field encryption primed and activated ({Profile}).",
                string.IsNullOrEmpty(_profile) ? "default profile" : "profile '" + _profile + "'");
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Rotates a Vault-backed encryptor's key on a background interval (one-shot timer re-armed each tick).</summary>
    internal sealed class VaultEncryptionRotationService : IHostedService, IDisposable
    {
        private readonly IVaultRotatableEncryptor _encryptor;
        private readonly TimeSpan _interval;
        private readonly ILogger? _logger;
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
            _timer = new Timer(async _ => await TickAsync().ConfigureAwait(false), null, _interval, Timeout.InfiniteTimeSpan);
            return Task.CompletedTask;
        }

        private async Task TickAsync()
        {
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
                if (!_stopped)
                    try { _timer?.Change(_interval, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
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

        private void ScheduleNext()
        {
            if (_stopped) return;
            var delay = Internal.VaultRenewal.NextDelay(_provider.MinLeaseSeconds, _provider.Options.RefreshInterval);
            _logger?.LogInformation("Next Vault DB credential renewal in {Delay}.", delay);
            try { _timer?.Change(delay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* stopping */ }
        }

        private async Task RenewTickAsync()
        {
            try
            {
                _logger?.LogDebug("Renewing Vault DB credentials…");
                await _provider.RefreshAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Keep serving the last good credentials until the next attempt.
                _logger?.LogWarning(ex, "Vault DB credential renewal failed; keeping previous credentials.");
            }
            finally
            {
                ScheduleNext();
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
