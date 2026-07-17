using System.Threading;
using System.Threading.Tasks;
using Socigy.OpenSource.DB.Core.Encryption;

namespace Socigy.OpenSource.DB.HashiCorp
{
#nullable enable
    /// <summary>
    /// A Vault-backed <see cref="IFieldEncryptor"/> that loads/refreshes its key material from Vault. The
    /// priming hosted service calls <see cref="RefreshAsync"/> at startup and installs the encryptor as the
    /// ambient (default or profiled) one.
    /// </summary>
    internal interface IVaultPrimableEncryptor : IFieldEncryptor
    {
        Task RefreshAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>A Vault-backed encryptor whose key can be rotated (manually or by the background rotation service).</summary>
    internal interface IVaultRotatableEncryptor
    {
        Task RotateAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Loads one encryptor's key material and installs it under <see cref="Profile"/>. Registered once per
    /// encryption helper call, so a default plus any number of named profiles can coexist — the DI container
    /// keeps every registration of this interface, unlike <c>AddHostedService</c>, which de-duplicates by
    /// implementation type and would silently drop the second primer.
    /// </summary>
    internal interface IVaultEncryptionPrimer
    {
        /// <summary>The profile this primer activates; <see langword="null"/>/empty for the default encryptor.</summary>
        string? Profile { get; }

        /// <summary>
        /// Primes and activates the encryptor. Safe to call more than once: the work runs at most once per
        /// successful attempt, so <c>UseSocigyVaultEncryption()</c> and the later hosted-service start share it.
        /// </summary>
        Task PrimeAsync(CancellationToken cancellationToken = default);
    }
#nullable disable
}
