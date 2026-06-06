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
            var client = VaultClientFactory.Create(options);

            services.AddSingleton(sp => new VaultFieldEncryptor(
                client, options, sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Encryption")));
            services.TryAddSingleton<IFieldEncryptor>(sp => sp.GetRequiredService<VaultFieldEncryptor>());
            services.AddHostedService<VaultEncryptionPrimingService>();
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
            var client = VaultClientFactory.Create(options);

            services.AddSingleton(sp => new VaultDbCredentialsProvider(
                client, options, sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Credentials")));
            services.TryAddSingleton<IDbCredentialsProvider>(sp => sp.GetRequiredService<VaultDbCredentialsProvider>());
            services.AddHostedService<VaultCredentialsRenewalService>();
            return services;
        }
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
