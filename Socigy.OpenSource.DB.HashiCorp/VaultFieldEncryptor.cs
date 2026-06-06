using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Socigy.OpenSource.DB.Core.Diagnostics;
using Socigy.OpenSource.DB.Core.Encryption;
using VaultSharp;

namespace Socigy.OpenSource.DB.HashiCorp
{
#nullable enable
    /// <summary>
    /// <see cref="IFieldEncryptor"/> backed by HashiCorp Vault: the 32-byte data-encryption key is read
    /// from a Vault KV-v2 secret at startup (and on refresh) and used for local, synchronous
    /// AES-256-CBC + HMAC-SHA256 encryption via <see cref="AesFieldEncryptor"/>. This keeps per-field
    /// crypto local (no Vault round-trip per field) while the key itself is managed in Vault.
    /// <para>
    /// Rotating the key means updating the KV secret and re-encrypting existing rows (a deliberate v1
    /// limitation). A future enhancement can switch to a Transit data-key envelope so old rows remain
    /// decryptable across rotations.
    /// </para>
    /// </summary>
    public sealed class VaultFieldEncryptor : IFieldEncryptor
    {
        private readonly VaultClientProvider _clients;
        private readonly VaultEncryptionOptions _options;
        private readonly ILogger? _logger;
        private volatile AesFieldEncryptor? _aes;

        public VaultFieldEncryptor(VaultClientProvider clients, VaultEncryptionOptions options, ILogger? logger = null)
        {
            _clients = clients ?? throw new ArgumentNullException(nameof(clients));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;
        }

        /// <summary>Reads the key from Vault KV-v2 and (re)builds the local AES encryptor. Call at startup.</summary>
        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            // Trackable by admins via the "Socigy.OpenSource.DB" ActivitySource + ILogger.
            using var activity = SocigyDbInstrumentation.ActivitySource.StartActivity("vault.encryption.key.fetch", ActivityKind.Client);
            activity?.SetTag("vault.kv.mount", _options.KvMountPoint);
            activity?.SetTag("vault.kv.path", _options.KeySecretPath);
            try
            {
                var secret = await _clients.Client.V1.Secrets.KeyValue.V2
                    .ReadSecretAsync(path: _options.KeySecretPath, mountPoint: _options.KvMountPoint)
                    .ConfigureAwait(false);

                if (secret?.Data?.Data == null || !secret.Data.Data.TryGetValue(_options.KeyField, out var keyObj) || keyObj == null)
                    throw new InvalidOperationException(
                        $"Vault secret '{_options.KeySecretPath}' (mount '{_options.KvMountPoint}') has no field '{_options.KeyField}'.");

                byte[] key = Convert.FromBase64String(keyObj.ToString()!);
                _aes = new AesFieldEncryptor(key);
                _logger?.LogInformation("Loaded field-encryption key from Vault KV '{Mount}/{Path}'", _options.KvMountPoint, _options.KeySecretPath);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger?.LogError(ex, "Failed to load field-encryption key from Vault KV '{Mount}/{Path}'", _options.KvMountPoint, _options.KeySecretPath);
                throw;
            }
        }

        private AesFieldEncryptor Aes =>
            _aes ?? throw new InvalidOperationException("VaultFieldEncryptor is not initialized — call RefreshAsync() at startup (AddSocigyVaultEncryption does this).");

        public byte[] Encrypt(byte[] plaintext, byte[]? associatedData = null) => Aes.Encrypt(plaintext, associatedData);

        public byte[] Decrypt(byte[] ciphertext, byte[]? associatedData = null) => Aes.Decrypt(ciphertext, associatedData);
    }
#nullable disable
}
