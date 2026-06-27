using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Socigy.OpenSource.DB.Core.Diagnostics;
using Socigy.OpenSource.DB.Core.Encryption;

namespace Socigy.OpenSource.DB.HashiCorp
{
#nullable enable
    /// <summary>
    /// <see cref="IFieldEncryptor"/> implementing <b>EaaS-direct</b> mode: every field value is encrypted and
    /// decrypted by Vault's <c>transit</c> engine directly, stored as a <c>vault:vN:…</c> ciphertext. Rotating
    /// the Transit key keeps old rows decryptable (Transit retains old versions); old ciphertext can be upgraded
    /// to the newest version via <c>transit/rewrap</c> (exposed through <see cref="IReencryptableEncryptor"/> and
    /// the bulk re-encryptor) without exposing plaintext.
    /// <para>
    /// This makes a Vault round-trip on <i>every</i> field read and write, violating the usual local/synchronous
    /// <see cref="IFieldEncryptor"/> contract — it is intended for a small number of highly-sensitive columns
    /// (typically via a profile), not bulk scans. The table:column context binds via the Transit <c>context</c>
    /// field, so the key must be created with <c>derived=true</c>.
    /// </para>
    /// </summary>
    public sealed class VaultTransitFieldEncryptor : IFieldEncryptor, IVaultPrimableEncryptor, IVaultRotatableEncryptor, IReencryptableEncryptor
    {
        private const string VaultPrefix = "vault:v";

        private readonly VaultTransitClient _transit;
        private readonly VaultTransitEncryptionOptions _options;
        private readonly ILogger? _logger;
        private readonly DecryptCache _cache;
        private volatile int _latestVersion;
        private int _warned;

        public VaultTransitFieldEncryptor(VaultClientProvider clients, VaultTransitEncryptionOptions options, ILogger? logger = null)
        {
            if (clients == null) throw new ArgumentNullException(nameof(clients));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _transit = new VaultTransitClient(clients, options.TransitMountPoint);
            _logger = logger;
            _cache = new DecryptCache(Math.Max(0, options.DecryptCacheSize));
        }

        /// <summary>Verifies the Transit key exists, caches its latest version, and logs the per-field-round-trip warning once.</summary>
        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            using var activity = SocigyDbInstrumentation.ActivitySource.StartActivity("vault.encryption.key.fetch", ActivityKind.Client);
            activity?.SetTag("vault.transit.key", _options.TransitKeyName);
            try
            {
                _latestVersion = await _transit.ReadLatestVersionAsync(_options.TransitKeyName, cancellationToken).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _warned, 1) == 0)
                    _logger?.LogWarning(
                        "Vault Transit EaaS-direct encryption is active for key '{Key}': every [Encrypted] field read/write makes a " +
                        "Vault round-trip. Use this for a few highly-sensitive columns (via a profile), not bulk scans.",
                        _options.TransitKeyName);
                _logger?.LogInformation("Vault Transit key '{Key}' ready (latest version {Version}).", _options.TransitKeyName, _latestVersion);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger?.LogError(ex, "Failed to read Vault Transit key '{Key}'", _options.TransitKeyName);
                throw;
            }
        }

        /// <summary>Re-reads the Transit key's latest version (the key itself is rotated operator-side in Vault).</summary>
        public Task RotateAsync(CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);

        public byte[] Encrypt(byte[] plaintext, byte[]? associatedData = null)
        {
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
            // Sync-over-async: IFieldEncryptor is synchronous. Safe under ASP.NET Core (no sync context).
            string cipher = _transit.EncryptAsync(_options.TransitKeyName, plaintext, associatedData).GetAwaiter().GetResult();
            return Encoding.UTF8.GetBytes(cipher);
        }

        public byte[] Decrypt(byte[] ciphertext, byte[]? associatedData = null)
        {
            if (ciphertext == null) throw new ArgumentNullException(nameof(ciphertext));
            string cipher = Encoding.UTF8.GetString(ciphertext);

            string cacheKey = BuildCacheKey(cipher, associatedData);
            if (_cache.TryGet(cacheKey, out var cached)) return cached;

            byte[] plaintext = _transit.DecryptAsync(_options.TransitKeyName, cipher, associatedData).GetAwaiter().GetResult();
            _cache.Set(cacheKey, plaintext);
            return plaintext;
        }

        /// <inheritdoc/>
        public bool NeedsUpgrade(byte[] ciphertext)
        {
            if (ciphertext == null) return false;
            // _latestVersion is 0 until RefreshAsync has primed it. Returning false here would make a re-encryption
            // pass that runs before priming silently report "0 cells upgraded" while doing nothing. Fail loud so the
            // misconfiguration (NeedsUpgrade called before RefreshAsync) is visible instead of a silent no-op.
            if (_latestVersion <= 0)
                throw new InvalidOperationException(
                    "Transit EaaS encryptor is not primed (call RefreshAsync at startup) — cannot determine whether a value needs re-encryption.");
            int version = ParseVersion(Encoding.UTF8.GetString(ciphertext));
            return version > 0 && version < _latestVersion;
        }

        /// <inheritdoc/>
        public async Task<byte[]> UpgradeToCurrentAsync(byte[] ciphertext, byte[]? associatedData = null)
        {
            string cipher = Encoding.UTF8.GetString(ciphertext);
            string rewrapped = await _transit.RewrapAsync(_options.TransitKeyName, cipher, associatedData).ConfigureAwait(false);
            return Encoding.UTF8.GetBytes(rewrapped);
        }

        // Parses N from a "vault:vN:…" ciphertext; 0 if not parseable.
        private static int ParseVersion(string cipher)
        {
            if (cipher == null || !cipher.StartsWith(VaultPrefix, StringComparison.Ordinal)) return 0;
            int i = VaultPrefix.Length;
            int version = 0;
            while (i < cipher.Length && cipher[i] >= '0' && cipher[i] <= '9')
            {
                version = version * 10 + (cipher[i] - '0');
                i++;
            }
            return i < cipher.Length && cipher[i] == ':' ? version : 0;
        }

        private static string BuildCacheKey(string cipher, byte[]? associatedData)
            => associatedData == null || associatedData.Length == 0 ? cipher : cipher + "|" + Convert.ToBase64String(associatedData);

        /// <summary>Bounded FIFO cache of decrypt results to soften repeated reads of the same ciphertext.</summary>
        private sealed class DecryptCache
        {
            private readonly int _capacity;
            private readonly object _lock = new object();
            private readonly Dictionary<string, byte[]> _map;
            private readonly Queue<string> _order;

            public DecryptCache(int capacity)
            {
                _capacity = capacity;
                _map = new Dictionary<string, byte[]>(capacity > 0 ? Math.Min(capacity, 1024) : 0);
                _order = new Queue<string>();
            }

            // The cache owns private copies of every plaintext: a decrypted byte[] is handed straight back
            // to the caller (a byte[] column returns it verbatim), so storing or returning the same reference
            // would let a caller that mutates the array corrupt the cached value for every later read.
            public bool TryGet(string key, out byte[] value)
            {
                if (_capacity == 0) { value = null!; return false; }
                lock (_lock)
                {
                    if (_map.TryGetValue(key, out var stored)) { value = (byte[])stored.Clone(); return true; }
                    value = null!;
                    return false;
                }
            }

            public void Set(string key, byte[] value)
            {
                if (_capacity == 0) return;
                var copy = (byte[])value.Clone();
                lock (_lock)
                {
                    if (_map.ContainsKey(key)) { _map[key] = copy; return; }
                    while (_order.Count >= _capacity)
                        _map.Remove(_order.Dequeue());
                    _map[key] = copy;
                    _order.Enqueue(key);
                }
            }
        }
    }
#nullable disable
}
