using System;

namespace Socigy.OpenSource.DB.Core.Encryption
{
#nullable enable
    /// <summary>
    /// Encrypts and decrypts the raw bytes of a single <c>[Encrypted]</c> column value.
    /// <para>
    /// Implementations MUST be synchronous and local — these methods run inside the generated
    /// per-row materialization and parameter-binding code, once per encrypted field per row. Do NOT
    /// make a network call here (e.g. a Vault round-trip per field). Key management that needs the
    /// network (issuing/rotating a data-encryption key, leasing) belongs in the implementation's own
    /// background refresh, with the actual <see cref="Encrypt"/>/<see cref="Decrypt"/> staying local.
    /// </para>
    /// <para>Implementations should be authenticated (tamper-evident) and safe for concurrent use.</para>
    /// </summary>
    public interface IFieldEncryptor
    {
        /// <summary>Encrypts <paramref name="plaintext"/> and returns the self-describing ciphertext envelope.</summary>
        byte[] Encrypt(byte[] plaintext);

        /// <summary>Decrypts an envelope produced by <see cref="Encrypt"/>. Throws if the data is tampered with or undecryptable.</summary>
        byte[] Decrypt(byte[] ciphertext);
    }
#nullable disable
}
