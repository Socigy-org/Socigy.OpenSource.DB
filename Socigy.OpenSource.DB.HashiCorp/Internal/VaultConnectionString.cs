using System.Data.Common;

namespace Socigy.OpenSource.DB.HashiCorp.Internal
{
#nullable enable
    internal static class VaultConnectionString
    {
        /// <summary>
        /// Composes the final connection string from a base (host/port/pooling/...) plus a leased
        /// username/password. Built through <see cref="DbConnectionStringBuilder"/> so the credentials are
        /// correctly quoted/escaped — Vault-generated passwords routinely contain ';', '=', quotes and
        /// spaces, which naive concatenation would turn into a malformed or wrongly-parsed string.
        /// </summary>
        public static string Compose(string baseConnectionString, string username, string password)
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = baseConnectionString ?? string.Empty };
            builder["Username"] = username;
            builder["Password"] = password;
            return builder.ConnectionString;
        }
    }
#nullable disable
}
