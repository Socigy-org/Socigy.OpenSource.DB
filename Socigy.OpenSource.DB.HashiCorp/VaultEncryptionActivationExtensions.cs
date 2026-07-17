using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Socigy.OpenSource.DB.Core.Diagnostics;

namespace Socigy.OpenSource.DB.HashiCorp
{
#nullable enable
    /// <summary>
    /// Activates Vault-backed field encryption without waiting for the host to start.
    /// </summary>
    /// <remarks>
    /// The <c>AddSocigyVault*Encryption</c> helpers only register encryptors; the priming hosted service does not
    /// run until <c>app.Run()</c>. Anything that touches an <c>[Encrypted]</c> column before that — notably the
    /// documented <c>await app.EnsureLatest{Db}Migration()</c> between <c>Build()</c> and <c>Run()</c>, or any
    /// bootstrap/seed — would throw from <c>SocigyFieldEncryption.Require()</c>. Call this first:
    /// <code>
    /// var app = builder.Build();
    /// await app.UseSocigyVaultEncryption();      // primes + activates every registered profile
    /// await app.EnsureLatestMyDbMigration();
    /// app.Run();
    /// </code>
    /// The hosted service still runs and remains the refresh/renewal mechanism; because priming is idempotent it
    /// simply finds the work already done.
    /// </remarks>
    public static class VaultEncryptionActivationExtensions
    {
        /// <summary>
        /// Primes and activates every registered Vault encryptor (the default and every named profile), so
        /// <c>[Encrypted]</c> columns are usable immediately. Safe to call more than once.
        /// </summary>
        public static Task UseSocigyVaultEncryption(this IHost host, CancellationToken cancellationToken = default)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            return host.Services.UseSocigyVaultEncryption(cancellationToken);
        }

        /// <summary>
        /// <see cref="UseSocigyVaultEncryption(IHost, CancellationToken)"/> for an app that has no
        /// <see cref="IHost"/> (a worker, a test, or a manually built provider).
        /// </summary>
        public static async Task UseSocigyVaultEncryption(this IServiceProvider services, CancellationToken cancellationToken = default)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));

            int primed = 0;
            // Resolving the primers (not the hosted services) avoids constructing unrelated hosted services here.
            foreach (var primer in services.GetServices<IVaultEncryptionPrimer>())
            {
                await primer.PrimeAsync(cancellationToken).ConfigureAwait(false);
                primed++;
            }

            if (primed == 0)
                SocigyDbDiagnostics.GetLogger()?.LogWarning(
                    "UseSocigyVaultEncryption() found no Vault encryptors registered, so nothing was activated. " +
                    "Call AddSocigyVaultEncryption / AddSocigyVaultEnvelopeEncryption / AddSocigyVaultTransitEncryption first.");
        }
    }
#nullable disable
}
