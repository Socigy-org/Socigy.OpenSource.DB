using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Socigy.OpenSource.DB.HashiCorp.Internal
{
#nullable enable
    /// <summary>
    /// Registers the one shared <see cref="VaultClientProvider"/> that every Vault feature uses, and refuses
    /// to let two features disagree about how to reach Vault.
    ///
    /// <para>
    /// Each <c>AddSocigyVault*</c> helper used to call <c>TryAddSingleton</c> with its own options captured in
    /// the factory lambda. First registration wins, so a second helper's <c>Address</c>, <c>Token</c>,
    /// <c>AppRoleId</c> and <c>AppRoleSecretId</c> were captured in a lambda that was never invoked and
    /// silently discarded. Those are not incidental settings — they are the auth identity the process uses for
    /// the whole of its life, so pointing field encryption and database credentials at different Vault servers
    /// or AppRoles quietly used one of them for both, with nothing logged.
    /// </para>
    /// <para>
    /// Sharing one client is still the right default (it keeps a single token renewed). What changes is that
    /// divergence is now an error at registration time, where the developer can see it, rather than a silent
    /// choice. Identical settings — the overwhelmingly common case — stay silent.
    /// </para>
    /// </summary>
    internal static class VaultClientRegistration
    {
        /// <summary>
        /// Carries the first-registered connection settings so a later registration can be compared against
        /// them. Registered as a singleton instance (not a factory), so it is readable during registration.
        /// </summary>
        internal sealed class Marker
        {
            public Marker(VaultConnectionOptions options, string registeredBy)
            {
                Options = options;
                RegisteredBy = registeredBy;
            }

            public VaultConnectionOptions Options { get; }

            /// <summary>The <c>AddSocigyVault*</c> helper that registered the client, for the error message.</summary>
            public string RegisteredBy { get; }
        }

        public static void AddSharedClient(IServiceCollection services, VaultConnectionOptions options, string registeredBy)
        {
            var existing = (Marker?)services
                .FirstOrDefault(d => d.ServiceType == typeof(Marker))
                ?.ImplementationInstance;

            if (existing != null)
            {
                var conflicts = FindConflicts(existing.Options, options);
                if (conflicts.Count > 0)
                    throw new InvalidOperationException(
                        $"{registeredBy} was given Vault connection settings that conflict with those already " +
                        $"registered by {existing.RegisteredBy}: {string.Join("; ", conflicts)}. " +
                        "All Vault features in one application share a single client (so a single auth token is " +
                        "renewed), so they must agree on how to reach Vault. Register the connection once with " +
                        "AddSocigyVaultClient(...) and leave Address/Token/AppRole unset on the feature options, " +
                        "or make the settings identical. Feature-specific mounts and paths stay per-feature.");

                return; // already registered with compatible settings
            }

            services.AddSingleton(new Marker(options, registeredBy));
            services.TryAddSingleton(sp => new VaultClientProvider(options,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Socigy.OpenSource.DB.Vault.Client")));
        }

        private static List<string> FindConflicts(VaultConnectionOptions first, VaultConnectionOptions second)
        {
            var conflicts = new List<string>();

            void Compare(string name, string? a, string? b)
            {
                // A null/empty on either side means "not configured here", which is not a disagreement — it is
                // the normal shape when one helper carries the connection settings and another does not.
                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return;
                if (!string.Equals(a, b, StringComparison.Ordinal))
                    conflicts.Add($"{name} differs");
            }

            Compare(nameof(VaultConnectionOptions.Address), first.Address, second.Address);
            Compare(nameof(VaultConnectionOptions.AppRoleId), first.AppRoleId, second.AppRoleId);

            // Values are compared but never quoted back: a token or an AppRole secret in an exception message
            // ends up in logs and crash reports.
            Compare(nameof(VaultConnectionOptions.Token), first.Token, second.Token);
            Compare(nameof(VaultConnectionOptions.AppRoleSecretId), first.AppRoleSecretId, second.AppRoleSecretId);

            return conflicts;
        }
    }
#nullable disable
}
