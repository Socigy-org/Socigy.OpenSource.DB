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
#nullable disable
}
