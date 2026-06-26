using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Socigy.OpenSource.DB.Core.Encryption
{
#nullable enable
    /// <summary>
    /// An <see cref="IFieldEncryptor"/> over a <b>versioned keyring</b> of data-encryption keys (DEKs). Each
    /// value's envelope embeds the id of the DEK that produced it, so after a key rotation old rows stay
    /// readable (their DEK still resolves) while new writes use the current DEK — no bulk re-encryption needed.
    /// Per-field crypto is fully local: this composes one <see cref="AesFieldEncryptor"/> per DEK and never
    /// makes a network call. The keyring itself is populated out-of-band (e.g. by unwrapping Transit-wrapped
    /// DEKs at startup/rotation).
    /// <para>
    /// Envelope layout: <c>[version:2][keyId:4 big-endian]</c> followed by the selected DEK's standard
    /// <see cref="AesFieldEncryptor"/> envelope. The key id is folded into the associated data passed to the
    /// inner encryptor, so it is authenticated by the existing HMAC.
    /// </para>
    /// </summary>
    public sealed class KeyringFieldEncryptor : IFieldEncryptor, IReencryptableEncryptor, IDisposable
    {
        private const byte EnvelopeVersion = 2;
        private const int HeaderSize = 1 + 4; // version + keyId

        private readonly Dictionary<int, AesFieldEncryptor> _byId;
        private readonly int _currentId;
        private readonly AesFieldEncryptor _current;
        private bool _disposed;

        /// <summary>
        /// Builds the keyring from raw DEK bytes keyed by version id, with <paramref name="currentId"/> selecting
        /// the DEK used for new writes. Every value must be a valid AES master key (16+ bytes; 32 recommended).
        /// </summary>
        public KeyringFieldEncryptor(IReadOnlyDictionary<int, byte[]> deksById, int currentId)
        {
            if (deksById == null) throw new ArgumentNullException(nameof(deksById));
            if (deksById.Count == 0) throw new ArgumentException("The keyring must contain at least one DEK.", nameof(deksById));

            _byId = new Dictionary<int, AesFieldEncryptor>(deksById.Count);
            foreach (var kv in deksById)
                _byId[kv.Key] = new AesFieldEncryptor(kv.Value);

            if (!_byId.TryGetValue(currentId, out var cur))
                throw new ArgumentException($"currentId {currentId} is not present in the keyring.", nameof(currentId));

            _currentId = currentId;
            _current = cur;
        }

        /// <summary>The id of the DEK used to encrypt new values.</summary>
        public int CurrentKeyId => _currentId;

        public byte[] Encrypt(byte[] plaintext, byte[]? associatedData = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(KeyringFieldEncryptor));
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));

            byte[] keyIdBytes = KeyIdToBytes(_currentId);
            byte[] inner = _current.Encrypt(plaintext, BindKeyId(keyIdBytes, associatedData));

            var output = new byte[HeaderSize + inner.Length];
            output[0] = EnvelopeVersion;
            Buffer.BlockCopy(keyIdBytes, 0, output, 1, 4);
            Buffer.BlockCopy(inner, 0, output, HeaderSize, inner.Length);
            return output;
        }

        public byte[] Decrypt(byte[] ciphertext, byte[]? associatedData = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(KeyringFieldEncryptor));
            if (ciphertext == null) throw new ArgumentNullException(nameof(ciphertext));
            if (ciphertext.Length < HeaderSize || ciphertext[0] != EnvelopeVersion)
                throw new CryptographicException("The encrypted value is malformed or was produced by an incompatible encryptor.");

            int keyId = KeyIdFromBytes(ciphertext, 1);
            if (!_byId.TryGetValue(keyId, out var enc))
                throw new CryptographicException(
                    $"The encrypted value references key version {keyId}, which is not present in the keyring (it may have been retired).");

            byte[] keyIdBytes = new byte[4];
            Buffer.BlockCopy(ciphertext, 1, keyIdBytes, 0, 4);

            byte[] inner = new byte[ciphertext.Length - HeaderSize];
            Buffer.BlockCopy(ciphertext, HeaderSize, inner, 0, inner.Length);
            return enc.Decrypt(inner, BindKeyId(keyIdBytes, associatedData));
        }

        /// <inheritdoc/>
        public bool NeedsUpgrade(byte[] ciphertext)
        {
            if (!TryGetKeyId(ciphertext, out int keyId)) return true; // unknown format -> let Force decide
            return keyId != _currentId;
        }

        /// <inheritdoc/>
        public Task<byte[]> UpgradeToCurrentAsync(byte[] ciphertext, byte[]? associatedData = null)
        {
            byte[] plaintext = Decrypt(ciphertext, associatedData);
            return Task.FromResult(Encrypt(plaintext, associatedData));
        }

        /// <summary>Reads the embedded key id from a keyring envelope without decrypting. Returns false if not one.</summary>
        public static bool TryGetKeyId(byte[] ciphertext, out int keyId)
        {
            if (ciphertext != null && ciphertext.Length >= HeaderSize && ciphertext[0] == EnvelopeVersion)
            {
                keyId = KeyIdFromBytes(ciphertext, 1);
                return true;
            }
            keyId = 0;
            return false;
        }

        // The DEK id is prepended to the caller's AAD so it is covered by the inner HMAC: tampering with the id
        // selects a different DEK whose MAC then fails, and an empty caller AAD still binds the id.
        private static byte[] BindKeyId(byte[] keyIdBytes, byte[]? associatedData)
        {
            if (associatedData == null || associatedData.Length == 0) return keyIdBytes;
            var combined = new byte[keyIdBytes.Length + associatedData.Length];
            Buffer.BlockCopy(keyIdBytes, 0, combined, 0, keyIdBytes.Length);
            Buffer.BlockCopy(associatedData, 0, combined, keyIdBytes.Length, associatedData.Length);
            return combined;
        }

        private static byte[] KeyIdToBytes(int id) =>
            new byte[] { (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id };

        private static int KeyIdFromBytes(byte[] buffer, int offset) =>
            (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];

        public void Dispose()
        {
            if (_disposed) return;
            foreach (var enc in _byId.Values)
                enc.Dispose();
            _disposed = true;
        }
    }
#nullable disable
}
