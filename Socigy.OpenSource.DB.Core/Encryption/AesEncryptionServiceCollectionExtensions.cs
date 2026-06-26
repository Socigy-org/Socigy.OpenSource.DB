using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Socigy.OpenSource.DB.Core.Encryption
{
#nullable enable
    /// <summary>
    /// DI helpers for the built-in <see cref="AesFieldEncryptor"/> — the non-KMS option for <c>[Encrypted]</c>
    /// columns. These install the ambient <see cref="SocigyFieldEncryption"/> encryptor at registration time
    /// (the key is available synchronously, so no startup hosted service is needed, unlike the Vault helpers).
    /// </summary>
    public static class AesEncryptionServiceCollectionExtensions
    {
        /// <summary>
        /// Registers an AES-256-CBC + HMAC-SHA256 field encryptor built from a 32-byte <paramref name="key"/>
        /// (from your secret store; never hard-coded) and installs it as the ambient encryptor — the default when
        /// <paramref name="profile"/> is null/empty, or under that named profile for
        /// <c>[Encrypted(Profile = "…")]</c> columns. Call once at startup.
        /// </summary>
        public static IServiceCollection AddSocigyAesEncryption(this IServiceCollection services, byte[] key, string? profile = null)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (key == null) throw new ArgumentNullException(nameof(key));

            var encryptor = new AesFieldEncryptor(key);
            SocigyFieldEncryption.Configure(profile, encryptor);
            // Only the default-profile encryptor claims the bare IFieldEncryptor service; named profiles are
            // reached through the ambient registry by name (mirrors the Vault DI helpers).
            if (string.IsNullOrEmpty(profile))
                services.TryAddSingleton<IFieldEncryptor>(encryptor);
            return services;
        }

        /// <summary>Overload accepting a Base64-encoded 32-byte key.</summary>
        public static IServiceCollection AddSocigyAesEncryption(this IServiceCollection services, string base64Key, string? profile = null)
        {
            if (base64Key == null) throw new ArgumentNullException(nameof(base64Key));
            return services.AddSocigyAesEncryption(Convert.FromBase64String(base64Key), profile);
        }
    }
#nullable disable
}
