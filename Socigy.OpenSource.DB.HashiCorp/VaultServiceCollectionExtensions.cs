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
            services.AddHostedService<VaultEncryptionPrimingService>();
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

    /// <summary>Loads the encryption key from Vault at startup and installs the ambient encryptor.</summary>
    internal sealed class VaultEncryptionPrimingService : IHostedService
    {
        private readonly VaultFieldEncryptor _encryptor;
        private readonly ILogger<VaultEncryptionPrimingService>? _logger;

        public VaultEncryptionPrimingService(VaultFieldEncryptor encryptor, ILogger<VaultEncryptionPrimingService>? logger = null)
        {
            _encryptor = encryptor;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _encryptor.RefreshAsync(cancellationToken).ConfigureAwait(false);
            SocigyFieldEncryption.Configure(_encryptor);
            _logger?.LogInformation("Vault field encryption primed and activated.");
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
