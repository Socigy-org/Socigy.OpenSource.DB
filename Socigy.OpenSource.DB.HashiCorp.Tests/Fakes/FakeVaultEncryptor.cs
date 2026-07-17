using System;
using System.Threading;
using System.Threading.Tasks;

namespace Socigy.OpenSource.DB.HashiCorp.Tests.Fakes;

/// <summary>
/// A Vault-backed encryptor stand-in that touches no network: it counts prime/rotate calls so the DI,
/// activation, and rotation-scheduling tests can assert lifecycle behavior without a live Vault.
/// </summary>
internal sealed class FakeVaultEncryptor : IVaultPrimableEncryptor, IVaultRotatableEncryptor
{
    private int _refreshCount;
    private int _rotateCount;

    public int RefreshCount => Volatile.Read(ref _refreshCount);
    public int RotateCount => Volatile.Read(ref _rotateCount);

    /// <summary>When set, <see cref="RefreshAsync"/> fails with it (still counted), simulating a Vault outage.</summary>
    public Exception? FailWith { get; set; }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _refreshCount);
        var fail = FailWith;
        return fail == null ? Task.CompletedTask : Task.FromException(fail);
    }

    public Task RotateAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _rotateCount);
        return Task.CompletedTask;
    }

    // The encryptor surface is irrelevant to these tests; pass values through untouched.
    public byte[] Encrypt(byte[] plaintext, byte[]? associatedData = null) => plaintext;
    public byte[] Decrypt(byte[] ciphertext, byte[]? associatedData = null) => ciphertext;
}
