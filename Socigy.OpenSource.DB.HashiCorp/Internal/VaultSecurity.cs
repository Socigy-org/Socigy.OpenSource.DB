using System;

namespace Socigy.OpenSource.DB.HashiCorp.Internal
{
#nullable enable
    internal static class VaultSecurity
    {
        /// <summary>
        /// True when the Vault address talks plaintext HTTP to a non-loopback host — tokens, keys and leased
        /// credentials would travel unencrypted over the network. Loopback (localhost/127.0.0.1/::1) is fine
        /// for local development, and https is always fine.
        /// </summary>
        public static bool IsInsecureRemote(string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) return false;
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return false;
            if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)) return false;
            return !uri.IsLoopback;
        }
    }
#nullable disable
}
