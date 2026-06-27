using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Socigy.OpenSource.DB.Core.Diagnostics;
using VaultSharp.V1.SecretsEngines.Transit;

namespace Socigy.OpenSource.DB.HashiCorp
{
#nullable enable
    /// <summary>
    /// Thin async wrapper over the subset of HashiCorp Vault's <c>transit</c> secrets engine this library uses.
    /// All VaultSharp Transit calls live here so the encryptors depend on a small, stable surface and any
    /// VaultSharp signature drift is fixed in one place. Each method notes the underlying Vault HTTP endpoint.
    /// <para>
    /// Verify against VaultSharp 1.17.5.1: <c>ITransitSecretsEngine.GenerateDataKeyAsync</c>,
    /// <c>EncryptAsync</c>, <c>DecryptAsync</c>, <c>RewrapAsync</c>, <c>ReadEncryptionKeyAsync</c> and the
    /// option/response property names used below.
    /// </para>
    /// </summary>
    internal sealed class VaultTransitClient
    {
        private readonly VaultClientProvider _clients;
        private readonly string _mountPoint;

        public VaultTransitClient(VaultClientProvider clients, string mountPoint)
        {
            _clients = clients ?? throw new ArgumentNullException(nameof(clients));
            _mountPoint = string.IsNullOrEmpty(mountPoint) ? "transit" : mountPoint;
        }

        /// <summary>
        /// <c>POST transit/datakey/plaintext/:name</c> — mints a fresh 256-bit data key, returning the
        /// plaintext key bytes (for local use) and the Vault-wrapped form (safe to persist).
        /// </summary>
        public async Task<(byte[] Plaintext, string Wrapped)> GenerateDataKeyAsync(string keyName, CancellationToken cancellationToken = default)
        {
            using var activity = SocigyDbInstrumentation.ActivitySource.StartActivity("vault.transit.datakey", ActivityKind.Client);
            activity?.SetTag("vault.transit.mount", _mountPoint);
            activity?.SetTag("vault.transit.key", keyName);

            // VaultSharp's single overload returns the plaintext data key (transit/datakey/plaintext/:name);
            // the key type is implicit, so there is no positional keyType argument.
            var options = new DataKeyRequestOptions { Bits = 256 };
            var result = await _clients.Client.V1.Secrets.Transit
                .GenerateDataKeyAsync(keyName, options, mountPoint: _mountPoint)
                .ConfigureAwait(false);

            byte[] plaintext = Convert.FromBase64String(result.Data.Base64EncodedPlainText);
            return (plaintext, result.Data.CipherText);
        }

        /// <summary><c>POST transit/decrypt/:name</c> on a wrapped data key — unwraps it back to plaintext bytes.</summary>
        public async Task<byte[]> UnwrapDataKeyAsync(string keyName, string wrapped, CancellationToken cancellationToken = default)
        {
            using var activity = SocigyDbInstrumentation.ActivitySource.StartActivity("vault.transit.unwrap", ActivityKind.Client);
            activity?.SetTag("vault.transit.mount", _mountPoint);
            activity?.SetTag("vault.transit.key", keyName);

            var options = new DecryptRequestOptions { CipherText = wrapped };
            var result = await _clients.Client.V1.Secrets.Transit
                .DecryptAsync(keyName, options, mountPoint: _mountPoint)
                .ConfigureAwait(false);

            return Convert.FromBase64String(result.Data.Base64EncodedPlainText);
        }

        /// <summary>
        /// <c>POST transit/encrypt/:name</c> — encrypts <paramref name="plaintext"/>, binding
        /// <paramref name="associatedData"/> via the transit <c>context</c> (requires a key created with
        /// <c>derived=true</c>). Returns the <c>vault:vN:…</c> ciphertext.
        /// </summary>
        public async Task<string> EncryptAsync(string keyName, byte[] plaintext, byte[]? associatedData, CancellationToken cancellationToken = default)
        {
            using var activity = SocigyDbInstrumentation.ActivitySource.StartActivity("vault.transit.encrypt", ActivityKind.Client);
            activity?.SetTag("vault.transit.mount", _mountPoint);
            activity?.SetTag("vault.transit.key", keyName);

            var options = new EncryptRequestOptions
            {
                Base64EncodedPlainText = Convert.ToBase64String(plaintext),
                Base64EncodedContext = ToBase64Context(associatedData),
            };
            var result = await _clients.Client.V1.Secrets.Transit
                .EncryptAsync(keyName, options, mountPoint: _mountPoint)
                .ConfigureAwait(false);

            return result.Data.CipherText;
        }

        /// <summary><c>POST transit/decrypt/:name</c> — decrypts a <c>vault:vN:…</c> ciphertext with the same context.</summary>
        public async Task<byte[]> DecryptAsync(string keyName, string cipherText, byte[]? associatedData, CancellationToken cancellationToken = default)
        {
            using var activity = SocigyDbInstrumentation.ActivitySource.StartActivity("vault.transit.decrypt", ActivityKind.Client);
            activity?.SetTag("vault.transit.mount", _mountPoint);
            activity?.SetTag("vault.transit.key", keyName);

            var options = new DecryptRequestOptions
            {
                CipherText = cipherText,
                Base64EncodedContext = ToBase64Context(associatedData),
            };
            var result = await _clients.Client.V1.Secrets.Transit
                .DecryptAsync(keyName, options, mountPoint: _mountPoint)
                .ConfigureAwait(false);

            return Convert.FromBase64String(result.Data.Base64EncodedPlainText);
        }

        /// <summary>
        /// <c>POST transit/rewrap/:name</c> — re-encrypts an existing ciphertext under the key's latest version
        /// without exposing plaintext. Returns the upgraded <c>vault:vN:…</c> ciphertext.
        /// </summary>
        public async Task<string> RewrapAsync(string keyName, string cipherText, byte[]? associatedData, CancellationToken cancellationToken = default)
        {
            using var activity = SocigyDbInstrumentation.ActivitySource.StartActivity("vault.transit.rewrap", ActivityKind.Client);
            activity?.SetTag("vault.transit.mount", _mountPoint);
            activity?.SetTag("vault.transit.key", keyName);

            var options = new RewrapRequestOptions
            {
                CipherText = cipherText,
                Base64EncodedContext = ToBase64Context(associatedData),
            };
            var result = await _clients.Client.V1.Secrets.Transit
                .RewrapAsync(keyName, options, mountPoint: _mountPoint)
                .ConfigureAwait(false);

            return result.Data.CipherText;
        }

        /// <summary><c>GET transit/keys/:name</c> — returns the key's latest version number.</summary>
        public async Task<int> ReadLatestVersionAsync(string keyName, CancellationToken cancellationToken = default)
        {
            using var activity = SocigyDbInstrumentation.ActivitySource.StartActivity("vault.transit.key.read", ActivityKind.Client);
            activity?.SetTag("vault.transit.mount", _mountPoint);
            activity?.SetTag("vault.transit.key", keyName);

            var result = await _clients.Client.V1.Secrets.Transit
                .ReadEncryptionKeyAsync(keyName, mountPoint: _mountPoint)
                .ConfigureAwait(false);

            return result.Data.LatestVersion;
        }

        // Transit's `context` field carries the associated data; null/empty AAD -> no context.
        private static string? ToBase64Context(byte[]? associatedData)
            => associatedData == null || associatedData.Length == 0 ? null : Convert.ToBase64String(associatedData);
    }

    /// <summary>
    /// Versioned keyring of Vault-wrapped data-encryption keys, persisted as a single KV-v2 field. Serialized as
    /// <c>current=&lt;id&gt;;&lt;id&gt;=&lt;wrapped&gt;;…</c> — a delimiter format chosen so it never collides with
    /// the base64/colon characters in a <c>vault:vN:…</c> wrapped key (no JSON dependency needed).
    /// </summary>
    internal sealed class VaultKeyring
    {
        public int Current { get; set; }
        public Dictionary<int, string> Keys { get; } = new Dictionary<int, string>();

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("current=").Append(Current);
            foreach (var kv in Keys)
                sb.Append(';').Append(kv.Key).Append('=').Append(kv.Value);
            return sb.ToString();
        }

        public static VaultKeyring Parse(string serialized)
        {
            if (string.IsNullOrEmpty(serialized))
                throw new FormatException("Empty keyring.");

            var keyring = new VaultKeyring();
            foreach (var part in serialized.Split(';'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('='); // value may contain further '=' (base64 padding); split on the first only
                if (eq < 0) throw new FormatException($"Malformed keyring entry '{part}'.");
                string key = part.Substring(0, eq);
                string value = part.Substring(eq + 1);
                if (key == "current")
                    keyring.Current = ParseId(value, part);
                else
                    keyring.Keys[ParseId(key, part)] = value;
            }
            if (keyring.Keys.Count == 0)
                throw new FormatException("Keyring has no keys.");
            return keyring;

            // Culture-invariant, overflow-safe id parse so a corrupted KV field fails as a clear
            // "malformed keyring" rather than an unscoped FormatException/OverflowException.
            static int ParseId(string text, string part)
                => int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id)
                    ? id
                    : throw new FormatException($"Malformed keyring entry '{part}'.");
        }

        public int NextId()
        {
            int max = 0;
            foreach (var id in Keys.Keys)
                if (id > max) max = id;
            return max + 1;
        }
    }
#nullable disable
}
