using System;
using System.Security.Cryptography;
using System.Text;

namespace Socigy.OpenSource.DB.Core.Encryption
{
#nullable enable
    /// <summary>
    /// Built-in authenticated symmetric <see cref="IFieldEncryptor"/>: AES-256-CBC + HMAC-SHA256 in
    /// encrypt-then-MAC order. Used when you don't have an external KMS — configure it once with a 32-byte
    /// key from your secret store (never hard-code keys).
    /// <para>
    /// AES-GCM would be preferable but is not available on <c>netstandard2.0</c> (the Core target); CBC
    /// with a separate HMAC over the IV+ciphertext is equivalently authenticated. Two independent sub-keys
    /// (one for AES, one for HMAC) are derived from the master key via an HMAC-SHA256 PRF.
    /// </para>
    /// Envelope layout: <c>[version:1][iv:16][ciphertext:n][mac:32]</c>.
    /// </summary>
    public sealed class AesFieldEncryptor : IFieldEncryptor
    {
        private const byte Version = 1;
        private const int IvSize = 16;
        private const int MacSize = 32;

        private readonly byte[] _encKey; // 32 bytes -> AES-256
        private readonly byte[] _macKey; // 32 bytes -> HMAC-SHA256

        /// <summary>Creates the encryptor from a master key (16 bytes or more; 32+ recommended).</summary>
        public AesFieldEncryptor(byte[] masterKey)
        {
            if (masterKey == null) throw new ArgumentNullException(nameof(masterKey));
            if (masterKey.Length < 16)
                throw new ArgumentException("The encryption key must be at least 16 bytes (32 recommended).", nameof(masterKey));

            _encKey = Derive(masterKey, "Socigy.Field.Enc.v1");
            _macKey = Derive(masterKey, "Socigy.Field.Mac.v1");
        }

        /// <summary>Creates the encryptor from a Base64-encoded master key.</summary>
        public AesFieldEncryptor(string base64MasterKey)
            : this(Convert.FromBase64String(base64MasterKey ?? throw new ArgumentNullException(nameof(base64MasterKey)))) { }

        public byte[] Encrypt(byte[] plaintext)
        {
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));

            byte[] iv = new byte[IvSize];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(iv);

            byte[] cipher;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _encKey;
                aes.IV = iv;
                using (var enc = aes.CreateEncryptor())
                    cipher = enc.TransformFinalBlock(plaintext, 0, plaintext.Length);
            }

            // Output = version || iv || cipher || mac(version || iv || cipher)
            var output = new byte[1 + IvSize + cipher.Length + MacSize];
            output[0] = Version;
            Buffer.BlockCopy(iv, 0, output, 1, IvSize);
            Buffer.BlockCopy(cipher, 0, output, 1 + IvSize, cipher.Length);

            int macInputLen = 1 + IvSize + cipher.Length;
            byte[] mac = ComputeMac(output, macInputLen);
            Buffer.BlockCopy(mac, 0, output, macInputLen, MacSize);
            return output;
        }

        public byte[] Decrypt(byte[] ciphertext)
        {
            if (ciphertext == null) throw new ArgumentNullException(nameof(ciphertext));
            if (ciphertext.Length < 1 + IvSize + MacSize || ciphertext[0] != Version)
                throw new CryptographicException("The encrypted value is malformed or was produced by an incompatible encryptor.");

            int macOffset = ciphertext.Length - MacSize;
            byte[] expectedMac = ComputeMac(ciphertext, macOffset);
            if (!FixedTimeEquals(ciphertext, macOffset, expectedMac))
                throw new CryptographicException("The encrypted value failed its integrity check (wrong key or tampered data).");

            int cipherLen = macOffset - (1 + IvSize);
            byte[] iv = new byte[IvSize];
            Buffer.BlockCopy(ciphertext, 1, iv, 0, IvSize);

            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _encKey;
                aes.IV = iv;
                using (var dec = aes.CreateDecryptor())
                    return dec.TransformFinalBlock(ciphertext, 1 + IvSize, cipherLen);
            }
        }

        private byte[] ComputeMac(byte[] buffer, int length)
        {
            using (var hmac = new HMACSHA256(_macKey))
                return hmac.ComputeHash(buffer, 0, length);
        }

        private static byte[] Derive(byte[] masterKey, string label)
        {
            using (var hmac = new HMACSHA256(masterKey))
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(label)); // 32-byte sub-key
        }

        // Constant-time compare of expectedMac against buffer[offset .. offset+32). (CryptographicOperations
        // .FixedTimeEquals is net5+, unavailable on netstandard2.0.)
        private static bool FixedTimeEquals(byte[] buffer, int offset, byte[] expectedMac)
        {
            int diff = 0;
            for (int i = 0; i < MacSize; i++)
                diff |= buffer[offset + i] ^ expectedMac[i];
            return diff == 0;
        }
    }
#nullable disable
}
