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
                // Do NOT dispose the previous keyring synchronously: a concurrent Encrypt/Decrypt may have already
                // captured it (via the volatile field) and be mid-operation, and disposal zeroes its key/MAC
                // arrays — which would make that in-flight call fail the MAC check (a CryptographicException on
                // perfectly valid data) or decrypt garbage. Defer disposal past a grace window so in-flight
                // operations (which finish in milliseconds) drain first, while still zeroing the old keys.
                if (previous != null)
                    _ = DisposeAfterGraceAsync(previous);

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

        // Disposes a rotated-out keyring after a grace window, so any Encrypt/Decrypt that captured it before the
        // swap has completed (such calls take milliseconds). Disposal only zeroes managed key/MAC byte arrays, so
        // deferring it never leaks an unmanaged resource; the keyring is otherwise GC-eligible.
        private static async Task DisposeAfterGraceAsync(IDisposable keyring)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
            catch { /* ignore */ }
            try { keyring.Dispose(); } catch { /* idempotent; best-effort key zeroing */ }
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
            switch (ClassifyKeyringRead(secret, _options.KeyringField, out var raw))
            {
                case KeyringReadState.Present:
                    return VaultKeyring.Parse(raw!);

                case KeyringReadState.ExistsButFieldEmpty:
                    // The secret exists but the configured keyring field is missing/empty. This is NOT a first run
                    // (a 404 is the only first-run signal, handled in TryReadKeyringSecretAsync) — overwriting it
                    // would discard whatever is there and, if a keyring was previously stored under another field
                    // name or partially written, make every already-encrypted row permanently undecryptable. Fail
                    // loud instead of bootstrapping over it.
                    throw new InvalidOperationException(
                        $"Vault secret at '{_options.KeyringSecretPath}' exists but its keyring field " +
                        $"'{_options.KeyringField}' is empty or missing. Refusing to overwrite it with a fresh " +
                        "keyring (that would discard any existing wrapped DEK versions and make encrypted rows " +
                        "undecryptable). Verify the KeyringField configuration matches how the keyring was written.");

                default: // KeyringReadState.FirstRun
                    break;
            }

            // First run: mint the initial DEK and persist a one-entry keyring.
            _logger?.LogInformation("No envelope keyring at '{Path}'; bootstrapping a new one.", _options.KeyringSecretPath);
            var (_, wrapped) = await _transit.GenerateDataKeyAsync(_options.TransitKeyName, cancellationToken).ConfigureAwait(false);
            var keyring = new VaultKeyring { Current = 1 };
            keyring.Keys[1] = wrapped;
            await WriteKeyringAsync(keyring, cancellationToken).ConfigureAwait(false);
            return keyring;
        }

        internal enum KeyringReadState { FirstRun, Present, ExistsButFieldEmpty }

        /// <summary>
        /// Classifies a keyring secret read. <see cref="KeyringReadState.FirstRun"/> (null secret, i.e. a genuine
        /// 404) is the ONLY state that may bootstrap-and-overwrite. A secret that exists but whose keyring field is
        /// missing/empty must NOT be overwritten (it would discard existing wrapped DEKs).
        /// </summary>
        internal static KeyringReadState ClassifyKeyringRead(IDictionary<string, object>? secret, string field, out string? raw)
        {
            raw = null;
            if (secret == null)
                return KeyringReadState.FirstRun;
            if (secret.TryGetValue(field, out var value) && value != null && !string.IsNullOrEmpty(value.ToString()))
            {
                raw = value.ToString();
                return KeyringReadState.Present;
            }
            return KeyringReadState.ExistsButFieldEmpty;
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
            catch (VaultSharp.Core.VaultApiException ex) when (IsSecretNotFound(ex))
            {
                // ONLY a real 404 (the secret genuinely doesn't exist yet) is treated as "first run" -> bootstrap.
                // Any other error (a transient 503 while Vault is sealed/standby, a 429, a network timeout, or a
                // KV policy that grants write but not read) must propagate: swallowing it returned null, and the
                // bootstrap path then OVERWROTE the existing keyring with a fresh current=1 DEK — discarding every
                // previously-wrapped DEK version and making all already-encrypted rows permanently undecryptable.
                return null;
            }
        }

        /// <summary>
        /// A keyring read may be treated as "first run" (-> bootstrap a new keyring) ONLY when Vault reports the
        /// secret genuinely does not exist (404). Any other failure must propagate so a transient/permission error
        /// never causes the existing keyring to be overwritten and the data to become undecryptable.
        /// </summary>
        internal static bool IsSecretNotFound(VaultSharp.Core.VaultApiException ex)
        {
            return ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound;
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
