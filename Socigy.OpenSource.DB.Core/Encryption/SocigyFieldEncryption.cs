using System;
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
    /// </summary>
    public static class SocigyFieldEncryption
    {
        private static volatile IFieldEncryptor? _current;

        /// <summary>
        /// The configured encryptor, or <see langword="null"/> if encryption has not been set up.
        /// Use this when you want to react to the absence of an encryptor; generated code uses
        /// <see cref="Require"/> so it fails with a clear message.
        /// </summary>
        public static IFieldEncryptor? Current => _current;

        /// <summary>Whether an encryptor has been configured.</summary>
        public static bool IsConfigured => _current != null;

        /// <summary>Sets the process-wide field encryptor. Thread-safe; the swap is atomic and lock-free for readers.</summary>
        public static void Configure(IFieldEncryptor encryptor)
        {
            _current = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
            SocigyDbDiagnostics.GetLogger()?.LogInformation(
                "Socigy field encryption configured with {EncryptorType}", encryptor.GetType().Name);
        }

        /// <summary>
        /// Returns the configured encryptor or throws a clear, actionable error. Called by generated code
        /// when an <c>[Encrypted]</c> column is actually accessed.
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
    }
#nullable disable
}
