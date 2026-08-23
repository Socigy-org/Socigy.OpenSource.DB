using System;
using System.Collections.Generic;

namespace Socigy.OpenSource.DB.HashiCorp
{
#nullable enable
    /// <summary>Common HashiCorp Vault connection + authentication settings.</summary>
    public abstract class VaultConnectionOptions
    {
        /// <summary>Vault server address, e.g. <c>https://vault.example.com:8200</c>.</summary>
        public string Address { get; set; } = "http://127.0.0.1:8200";

        /// <summary>A Vault token to authenticate with. Set this OR the AppRole pair.</summary>
        public string? Token { get; set; }

        /// <summary>AppRole role id (set together with <see cref="AppRoleSecretId"/>).</summary>
        public string? AppRoleId { get; set; }

        /// <summary>AppRole secret id (set together with <see cref="AppRoleId"/>).</summary>
        public string? AppRoleSecretId { get; set; }
    }

    /// <summary>
    /// Connection and authentication settings for the shared Vault client, registered once via
    /// <c>AddSocigyVaultClient</c> so several Vault features can be enabled without each repeating them.
    /// Carries nothing beyond <see cref="VaultConnectionOptions"/>: mounts, paths and key names are
    /// feature-specific and stay on each feature's own options.
    /// </summary>
    public sealed class VaultClientOptions : VaultConnectionOptions
    {
    }

    /// <summary>
    /// Settings for Vault-backed field encryption. The data-encryption key is read from a Vault KV-v2
    /// secret at startup and used for local AES-256-CBC+HMAC encryption (so per-field crypto stays
    /// synchronous and local — no Vault round-trip per field).
    /// </summary>
    public sealed class VaultEncryptionOptions : VaultConnectionOptions
    {
        /// <summary>KV-v2 secrets-engine mount point (default <c>secret</c>).</summary>
        public string KvMountPoint { get; set; } = "secret";

        /// <summary>Path of the KV-v2 secret that holds the encryption key (e.g. <c>socigy/db-key</c>).</summary>
        public string KeySecretPath { get; set; } = "socigy/db-encryption-key";

        /// <summary>Field name within the secret whose value is the Base64-encoded 32-byte key.</summary>
        public string KeyField { get; set; } = "key";
    }

    /// <summary>
    /// Common settings for the two Vault Transit-backed field-encryption modes (data-key envelope and
    /// EaaS-direct). Both encrypt <c>[Encrypted]</c> columns using Vault's <c>transit</c> engine.
    /// </summary>
    public abstract class VaultTransitOptions : VaultConnectionOptions
    {
        /// <summary>Transit secrets-engine mount point (default <c>transit</c>).</summary>
        public string TransitMountPoint { get; set; } = "transit";

        /// <summary>Name of the Transit key (the key-encryption key) to use (default <c>socigy-db</c>).</summary>
        public string TransitKeyName { get; set; } = "socigy-db";

        /// <summary>
        /// Optional encryptor <b>profile</b> to register this encryptor under. Leave <see langword="null"/> to
        /// install it as the default encryptor; set a name to route only <c>[Encrypted(Profile = "…")]</c>
        /// columns here (so e.g. envelope mode is the default and Transit EaaS covers a few sensitive columns).
        /// </summary>
        public string? Profile { get; set; }

        /// <summary>Run rotation automatically in the background on <see cref="RotationInterval"/> (default off).</summary>
        public bool EnableBackgroundRotation { get; set; }

        /// <summary>Interval for background rotation when <see cref="EnableBackgroundRotation"/> is set (default 90 days).</summary>
        public TimeSpan RotationInterval { get; set; } = TimeSpan.FromDays(90);
    }

    /// <summary>
    /// Settings for the <b>data-key envelope</b> mode: a versioned keyring of Transit-wrapped DEKs is kept in a
    /// KV-v2 secret; per-field crypto is local AES and Transit is only contacted at startup/rotation. Old rows
    /// stay readable across rotations because each value embeds the id of the DEK that produced it.
    /// </summary>
    public sealed class VaultEnvelopeEncryptionOptions : VaultTransitOptions
    {
        /// <summary>KV-v2 secrets-engine mount point that stores the keyring (default <c>secret</c>).</summary>
        public string KvMountPoint { get; set; } = "secret";

        /// <summary>Path of the KV-v2 secret that holds the wrapped-DEK keyring (default <c>socigy/db-keyring</c>).</summary>
        public string KeyringSecretPath { get; set; } = "socigy/db-keyring";

        /// <summary>Field within the secret that stores the serialized keyring (default <c>keyring</c>).</summary>
        public string KeyringField { get; set; } = "keyring";
    }

    /// <summary>
    /// Settings for the <b>EaaS-direct</b> mode: each field value is encrypted/decrypted by Vault Transit
    /// directly (<c>vault:vN:…</c> ciphertext), matching the HashiCorp rewrap tutorial. This makes a Vault
    /// round-trip per field, so it suits a few highly-sensitive columns (typically via <see cref="VaultTransitOptions.Profile"/>),
    /// not bulk scans. The Transit key must be created with <c>derived=true</c> so the table:column context binds.
    /// </summary>
    public sealed class VaultTransitEncryptionOptions : VaultTransitOptions
    {
        /// <summary>Max number of decrypt results cached in memory to soften repeated reads (default 10,000).</summary>
        public int DecryptCacheSize { get; set; } = 10_000;
    }

    /// <summary>
    /// Settings for Vault-managed (rotating) database credentials sourced from the Database secrets engine.
    /// </summary>
    public sealed class VaultCredentialsOptions : VaultConnectionOptions
    {
        /// <summary>Database secrets-engine mount point (default <c>database</c>).</summary>
        public string DatabaseMountPoint { get; set; } = "database";

        /// <summary>
        /// Maps each logical database name (the Socigy connection-factory key, e.g. "AuthDb") to the Vault
        /// database role that issues its credentials.
        /// </summary>
        public Dictionary<string, string> DatabaseRoles { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Base connection string with everything except the username/password (host, port, pooling, etc.),
        /// e.g. <c>Host=db;Port=5432;Pooling=true</c>. The leased <c>Username=</c>/<c>Password=</c> are appended.
        /// </summary>
        public string BaseConnectionString { get; set; } = "Host=127.0.0.1;Port=5432";

        /// <summary>How often to renew leased credentials in the background (default 30 minutes).</summary>
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Renew the existing lease in place while Vault still allows it, instead of leasing a fresh
        /// credential every round (default <see langword="true"/>).
        ///
        /// A renewal keeps the same username and password, so the connection string does not change and the
        /// database role is not re-minted. Turning this off restores the previous behaviour — every refresh
        /// mints a brand-new PostgreSQL user — which leaves up to <c>ceil(TTL / RefreshInterval) + 1</c>
        /// dynamic roles alive per database at once.
        /// </summary>
        public bool RenewLeases { get; set; } = true;

        /// <summary>
        /// Revoke the previous lease after successfully leasing a replacement (default <see langword="true"/>).
        ///
        /// Without this, a superseded credential stays valid until its TTL expires on its own, so the count of
        /// live dynamic roles is bounded only by the role's <c>default_ttl</c>. Revoking makes the handover
        /// explicit and immediate.
        /// </summary>
        public bool RevokeOnRefresh { get; set; } = true;

        /// <summary>
        /// Revoke every held lease when the provider is disposed, i.e. at host shutdown
        /// (default <see langword="true"/>).
        /// </summary>
        public bool RevokeOnShutdown { get; set; } = true;

        /// <summary>
        /// How long a single lease may be renewed for before it is replaced with a fresh one (default 24
        /// hours). Vault enforces its own <c>max_ttl</c> per role and will refuse to renew past it; this is
        /// the client-side bound, so set it at or below the role's <c>max_ttl</c> to avoid a failed renewal
        /// round-trip before the inevitable re-lease.
        /// </summary>
        public TimeSpan MaxLeaseLifetime { get; set; } = TimeSpan.FromHours(24);
    }
#nullable disable
}
