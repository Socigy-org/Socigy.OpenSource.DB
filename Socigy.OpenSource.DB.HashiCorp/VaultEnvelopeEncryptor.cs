using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Socigy.OpenSource.DB.Core.Diagnostics;
using Socigy.OpenSource.DB.Core.Encryption;

namespace Socigy.OpenSource.DB.HashiCorp
{
#nullable enable
    /// <summary>
    /// <see cref="IFieldEncryptor"/> implementing the <b>Transit data-key envelope</b> mode. A versioned keyring
    /// of data-encryption keys (DEKs) is kept in a Vault KV-v2 secret, each DEK wrapped by a Transit
    /// key-encryption key. At startup (and on rotation) the wrapped DEKs are unwrapped via Transit into an
    /// in-memory <see cref="KeyringFieldEncryptor"/>; from then on every field is encrypted/decrypted locally
    /// (no Vault round-trip per field). Each value embeds its DEK id, so after a rotation old rows stay readable
    /// while new writes use the newest DEK — no bulk re-encryption required.
    /// </summary>
    public sealed class VaultEnvelopeEncryptor : IFieldEncryptor, IVaultPrimableEncryptor, IVaultRotatableEncryptor
    {
        private readonly VaultClientProvider _clients;
        private readonly VaultEnvelopeEncryptionOptions _options;
        private readonly VaultTransitClient _transit;
        private readonly ILogger? _logger;
        private readonly SemaphoreSlim _rotationGate = new SemaphoreSlim(1, 1);
        private volatile KeyringFieldEncryptor? _keyring;

        public VaultEnvelopeEncryptor(VaultClientProvider clients, VaultEnvelopeEncryptionOptions options, ILogger? logger = null)
        {
            _clients = clients ?? throw new ArgumentNullException(nameof(clients));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _transit = new VaultTransitClient(clients, options.TransitMountPoint);
            _logger = logger;
        }

        /// <summary>Loads the keyring from Vault (bootstrapping it on first run) and builds the local encryptor.</summary>
        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            using var activity = SocigyDbInstrumentation.ActivitySource.StartActivity("vault.encryption.key.fetch", ActivityKind.Client);
            activity?.SetTag("vault.kv.mount", _options.KvMountPoint);
            activity?.SetTag("vault.kv.path", _options.KeyringSecretPath);
            try
            {
                VaultKeyring keyring = await LoadOrBootstrapKeyringAsync(cancellationToken).ConfigureAwait(false);

                var deks = new Dictionary<int, byte[]>(keyring.Keys.Count);
                foreach (var kv in keyring.Keys)
                    deks[kv.Key] = await _transit.UnwrapDataKeyAsync(_options.TransitKeyName, kv.Value, cancellationToken).ConfigureAwait(false);

                var previous = _keyring;
                _keyring = new KeyringFieldEncryptor(deks, keyring.Current);
                previous?.Dispose();

                _logger?.LogInformation(
                    "Loaded envelope keyring from Vault KV '{Mount}/{Path}' ({Count} key version(s), current={Current}).",
                    _options.KvMountPoint, _options.KeyringSecretPath, deks.Count, keyring.Current);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger?.LogError(ex, "Failed to load envelope keyring from Vault KV '{Mount}/{Path}'", _options.KvMountPoint, _options.KeyringSecretPath);
                throw;
            }
        }

        /// <summary>
        /// Mints a new DEK via Transit, appends it to the keyring as the new current version, persists the
        /// keyring, and rebuilds the local encryptor. Old DEKs are retained so existing rows stay readable.
        /// </summary>
        public async Task RotateAsync(CancellationToken cancellationToken = default)
        {
            await _rotationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var activity = SocigyDbInstrumentation.ActivitySource.StartActivity("vault.encryption.key.rotate", ActivityKind.Client);
                activity?.SetTag("vault.kv.path", _options.KeyringSecretPath);

                VaultKeyring keyring = await LoadOrBootstrapKeyringAsync(cancellationToken).ConfigureAwait(false);

                var (_, wrapped) = await _transit.GenerateDataKeyAsync(_options.TransitKeyName, cancellationToken).ConfigureAwait(false);
                int newId = keyring.NextId();
                keyring.Keys[newId] = wrapped;
                keyring.Current = newId;

                await WriteKeyringAsync(keyring, cancellationToken).ConfigureAwait(false);
                _logger?.LogInformation("Rotated envelope keyring: new current key version {Version}.", newId);

                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _rotationGate.Release();
            }
        }

        private async Task<VaultKeyring> LoadOrBootstrapKeyringAsync(CancellationToken cancellationToken)
        {
            var secret = await TryReadKeyringSecretAsync(cancellationToken).ConfigureAwait(false);
            if (secret != null && secret.TryGetValue(_options.KeyringField, out var raw) && raw != null && !string.IsNullOrEmpty(raw.ToString()))
                return VaultKeyring.Parse(raw.ToString()!);

            // First run: mint the initial DEK and persist a one-entry keyring.
            _logger?.LogInformation("No envelope keyring at '{Path}'; bootstrapping a new one.", _options.KeyringSecretPath);
            var (_, wrapped) = await _transit.GenerateDataKeyAsync(_options.TransitKeyName, cancellationToken).ConfigureAwait(false);
            var keyring = new VaultKeyring { Current = 1 };
            keyring.Keys[1] = wrapped;
            await WriteKeyringAsync(keyring, cancellationToken).ConfigureAwait(false);
            return keyring;
        }

        private async Task<IDictionary<string, object>?> TryReadKeyringSecretAsync(CancellationToken cancellationToken)
        {
            try
            {
                var secret = await _clients.Client.V1.Secrets.KeyValue.V2
                    .ReadSecretAsync(path: _options.KeyringSecretPath, mountPoint: _options.KvMountPoint)
                    .ConfigureAwait(false);
                return secret?.Data?.Data;
            }
            catch (VaultSharp.Core.VaultApiException)
            {
                // Most commonly a 404 because the keyring secret doesn't exist yet -> bootstrap a new one.
                // A genuine auth/permission problem will resurface on the subsequent bootstrap write.
                return null;
            }
        }

        private Task WriteKeyringAsync(VaultKeyring keyring, CancellationToken cancellationToken)
        {
            var data = new Dictionary<string, object> { [_options.KeyringField] = keyring.Serialize() };
            return _clients.Client.V1.Secrets.KeyValue.V2
                .WriteSecretAsync(path: _options.KeyringSecretPath, data: data, mountPoint: _options.KvMountPoint);
        }

        private KeyringFieldEncryptor Keyring =>
            _keyring ?? throw new InvalidOperationException(
                "VaultEnvelopeEncryptor is not initialized — call RefreshAsync() at startup (AddSocigyVaultEnvelopeEncryption does this).");

        public byte[] Encrypt(byte[] plaintext, byte[]? associatedData = null) => Keyring.Encrypt(plaintext, associatedData);

        public byte[] Decrypt(byte[] ciphertext, byte[]? associatedData = null) => Keyring.Decrypt(ciphertext, associatedData);
    }
#nullable disable
}
