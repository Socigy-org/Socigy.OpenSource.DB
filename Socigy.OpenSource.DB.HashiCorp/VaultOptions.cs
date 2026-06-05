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
    }
#nullable disable
}
