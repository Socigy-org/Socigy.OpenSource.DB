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
    /// <b>Plan the column split up front.</b> Because encryption is non-deterministic, an encrypted column can
    /// be read and written by primary key but can never appear in a <c>WHERE</c>, <c>ORDER BY</c>, <c>LIKE</c>
    /// or <c>SELECT</c> projection — doing so throws <see cref="NotSupportedException"/>. That makes
    /// <c>[Encrypted]</c> in practice a property of every query that touches the column, not just of the
    /// column, and the two live in different files. Any column you need to search must therefore either stay
    /// plaintext or be split in two:
    /// </para>
    /// <code>
    /// public string EntityLabel { get; set; }                        // searchable; non-sensitive labels only
    /// [Encrypted] public byte[]? EntityLabelEncrypted { get; set; }  // sensitive values; route on write, coalesce on read
    /// </code>
    /// <para>
    /// Deciding this at design time is much cheaper than discovering it later: retro-fitting <c>[Encrypted]</c>
    /// to a column that is already searched turns a data-protection improvement into a broken feature, and by
    /// the time the exception fires the schema is usually deployed. The migration generator also refuses to
    /// encrypt an existing populated column, because doing so is a two-phase, application-level data migration
    /// that no SQL statement can perform.
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
