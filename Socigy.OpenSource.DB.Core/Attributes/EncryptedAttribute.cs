using System;

namespace Socigy.OpenSource.DB.Attributes
{
#nullable enable
    /// <summary>
    /// Marks a property whose value must be encrypted at rest. The source generator stores the column as
    /// <c>bytea</c>; on write the value is encrypted, on read it is decrypted, using the ambient
    /// <see cref="Socigy.OpenSource.DB.Core.Encryption.IFieldEncryptor"/> configured via
    /// <see cref="Socigy.OpenSource.DB.Core.Encryption.SocigyFieldEncryption"/>.
    /// <para>
    /// Because encryption is non-deterministic, an encrypted column cannot be used in a <c>WHERE</c>,
    /// <c>ORDER BY</c>, or <c>LIKE</c> clause — doing so throws <see cref="NotSupportedException"/>.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class EncryptedAttribute : Attribute
    {
        /// <summary>
        /// When <see langword="true"/> (the default) the property is decrypted automatically on read.
        /// <para>
        /// Set to <see langword="false"/> to skip automatic decryption: the source generator instead fills a
        /// read-only <c>{Property}RawEncrypted</c> (<see cref="byte"/>[]) with the raw ciphertext, and adds a
        /// getter-only <c>{Property}Decrypted</c> that decrypts on first access and caches the result into the
        /// property itself. Useful when most reads don't need the plaintext and you want to avoid the
        /// per-row decryption cost.
        /// </para>
        /// </summary>
        public bool AutoDecrypt { get; set; } = true;

        /// <summary>
        /// Routes this column to a named encryptor <b>profile</b> instead of the default. Leave
        /// <see langword="null"/>/empty (the default) to use the ambient default encryptor; set it to a name
        /// registered via <c>SocigyFieldEncryption.Configure("name", encryptor)</c> (or a DI helper such as
        /// <c>AddSocigyVaultTransitEncryption(o => o.Profile = "name")</c>) to encrypt just this column with a
        /// different encryptor — e.g. a few highly-sensitive columns using Vault Transit while the rest use a
        /// local envelope encryptor.
        /// </summary>
        public string? Profile { get; set; }
    }
#nullable disable
}
