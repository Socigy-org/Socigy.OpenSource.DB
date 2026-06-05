using System;
using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.AppRole;
using VaultSharp.V1.AuthMethods.Token;

namespace Socigy.OpenSource.DB.HashiCorp
{
#nullable enable
    internal static class VaultClientFactory
    {
        /// <summary>Builds an <see cref="IVaultClient"/> from connection options (token or AppRole auth).</summary>
        public static IVaultClient Create(VaultConnectionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.Address))
                throw new ArgumentException("Vault Address must be set.", nameof(options));

            IAuthMethodInfo auth;
            if (!string.IsNullOrEmpty(options.Token))
                auth = new TokenAuthMethodInfo(options.Token);
            else if (!string.IsNullOrEmpty(options.AppRoleId) && !string.IsNullOrEmpty(options.AppRoleSecretId))
                auth = new AppRoleAuthMethodInfo(options.AppRoleId, options.AppRoleSecretId);
            else
                throw new ArgumentException("Vault auth not configured: set Token, or both AppRoleId and AppRoleSecretId.");

            return new VaultClient(new VaultClientSettings(options.Address, auth));
        }
    }
#nullable disable
}
