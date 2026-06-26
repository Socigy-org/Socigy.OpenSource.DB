using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Socigy.OpenSource.DB.Core.Diagnostics;

namespace Socigy.OpenSource.DB.Core.Encryption
{
#nullable enable
    /// <summary>
    /// Ambient entry point for field-level encryption. Generated entity code (e.g. <c>GetColumns()</c> and
    /// the static <c>ConvertFrom(...)</c> materializer) is plain POCO code with no access to DI, so it reads
    /// the configured <see cref="IFieldEncryptor"/> from here — exactly mirroring how
    /// <see cref="Diagnostics.SocigyDbDiagnostics"/> exposes ambient diagnostics options.
    /// <para>
    /// Configure this once at startup, before any <c>[Encrypted]</c> column is read or written — either
    /// directly via <see cref="Configure(IFieldEncryptor)"/>, or through a DI helper such as
    /// <c>AddSocigyAesEncryption(...)</c> / <c>AddSocigyVaultEncryption(...)</c> which call it for you.
    /// </para>
    /// <para>
    /// Besides the <b>default</b> encryptor, named <b>profiles</b> can be registered so individual
    /// <c>[Encrypted(Profile = "…")]</c> columns route to a different encryptor (e.g. most columns use a local
    /// envelope encryptor while a few highly-sensitive ones use a Vault Transit encryptor). Reads are lock-free:
    /// the profile table is swapped atomically by copy-on-write, matching the default encryptor's guarantee.
    /// </para>
    /// </summary>
    public static class SocigyFieldEncryption
    {
        private static volatile IFieldEncryptor? _current;
        // Immutable snapshot swapped atomically; readers never lock. Null until the first named profile is set.
        private static volatile Dictionary<string, IFieldEncryptor>? _profiles;
        private static readonly object _profileLock = new object();

        /// <summary>
        /// The configured default encryptor, or <see langword="null"/> if encryption has not been set up.
        /// Use this when you want to react to the absence of an encryptor; generated code uses
        /// <see cref="Require()"/> so it fails with a clear message.
        /// </summary>
        public static IFieldEncryptor? Current => _current;

        /// <summary>Whether the default encryptor has been configured.</summary>
        public static bool IsConfigured => _current != null;

        /// <summary>Sets the process-wide default field encryptor. Thread-safe; the swap is atomic and lock-free for readers.</summary>
        public static void Configure(IFieldEncryptor encryptor)
        {
            _current = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
            SocigyDbDiagnostics.GetLogger()?.LogInformation(
                "Socigy field encryption configured with {EncryptorType}", encryptor.GetType().Name);
        }

        /// <summary>
        /// Registers an encryptor under a named <paramref name="profile"/> (used by
        /// <c>[Encrypted(Profile = "…")]</c> columns). A null/empty profile configures the default encryptor,
        /// equivalent to <see cref="Configure(IFieldEncryptor)"/>. Thread-safe; lock-free for readers.
        /// </summary>
        public static void Configure(string? profile, IFieldEncryptor encryptor)
        {
            if (encryptor == null) throw new ArgumentNullException(nameof(encryptor));
            if (string.IsNullOrEmpty(profile))
            {
                Configure(encryptor);
                return;
            }

            lock (_profileLock)
            {
                var next = _profiles == null
                    ? new Dictionary<string, IFieldEncryptor>(StringComparer.Ordinal)
                    : new Dictionary<string, IFieldEncryptor>(_profiles, StringComparer.Ordinal);
                next[profile!] = encryptor;
                _profiles = next; // atomic publish
            }

            SocigyDbDiagnostics.GetLogger()?.LogInformation(
                "Socigy field encryption profile '{Profile}' configured with {EncryptorType}", profile, encryptor.GetType().Name);
        }

        /// <summary>
        /// Returns the default encryptor or throws a clear, actionable error. Called by generated code when a
        /// default-profile <c>[Encrypted]</c> column is accessed.
        /// </summary>
        public static IFieldEncryptor Require()
        {
            var current = _current;
            if (current == null)
                throw new InvalidOperationException(
                    "An [Encrypted] column was read or written, but no IFieldEncryptor is configured. " +
                    "Call SocigyFieldEncryption.Configure(new AesFieldEncryptor(key)) at startup, or use a DI " +
                    "helper such as AddSocigyAesEncryption(...) / AddSocigyVaultEncryption(...).");
            return current;
        }

        /// <summary>
        /// Resolves the encryptor for <paramref name="profile"/> (null/empty -> the default), or throws a clear
        /// error if that profile was never configured. Called by generated code for
        /// <c>[Encrypted(Profile = "…")]</c> columns.
        /// </summary>
        public static IFieldEncryptor Require(string? profile)
        {
            if (string.IsNullOrEmpty(profile)) return Require();

            var profiles = _profiles;
            if (profiles != null && profiles.TryGetValue(profile!, out var enc))
                return enc;

            throw new InvalidOperationException(
                $"An [Encrypted(Profile = \"{profile}\")] column was read or written, but no encryptor is " +
                $"configured for profile '{profile}'. Register one with SocigyFieldEncryption.Configure(\"{profile}\", encryptor) " +
                "at startup, or via a DI helper such as AddSocigyVaultTransitEncryption(o => o.Profile = \"" + profile + "\").");
        }
    }
#nullable disable
}
