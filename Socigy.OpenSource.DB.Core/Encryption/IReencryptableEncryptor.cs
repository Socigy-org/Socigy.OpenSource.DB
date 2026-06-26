using System.Threading.Tasks;

namespace Socigy.OpenSource.DB.Core.Encryption
{
#nullable enable
    /// <summary>
    /// Optional capability for an <see cref="IFieldEncryptor"/> whose ciphertext carries a key version, so a
    /// value can be detected as out-of-date and rewritten to the current key without exposing plaintext to the
    /// caller. Implemented by versioned encryptors (e.g. the Transit keyring / Transit EaaS modes) and consumed
    /// by the bulk re-encryption utility (<c>FieldReencryptor</c>) to skip already-current values.
    /// </summary>
    public interface IReencryptableEncryptor
    {
        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="ciphertext"/> was produced under an older key
        /// version than the current one (or is not in this encryptor's format) and should be upgraded.
        /// </summary>
        bool NeedsUpgrade(byte[] ciphertext);

        /// <summary>
        /// Rewrites <paramref name="ciphertext"/> to the current key version, preserving the same
        /// <paramref name="associatedData"/> binding. May be a purely local re-encryption (keyring mode) or a
        /// Vault round-trip (Transit rewrap); it is only ever called from out-of-band admin/maintenance code,
        /// never the per-row hot path.
        /// </summary>
        Task<byte[]> UpgradeToCurrentAsync(byte[] ciphertext, byte[]? associatedData = null);
    }
#nullable disable
}
