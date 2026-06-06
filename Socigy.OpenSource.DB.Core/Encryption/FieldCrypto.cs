using System;

namespace Socigy.OpenSource.DB.Core.Encryption
{
#nullable enable
    /// <summary>
    /// Glue used by generated entity code for <c>[Encrypted]</c> columns: turns a CLR value into an
    /// encrypted <c>byte[]</c> for writing, and an encrypted <c>byte[]</c> back into the CLR value for
    /// reading. <c>null</c>/<see cref="DBNull"/> pass through unchanged (an encrypted column that is NULL
    /// stays NULL — it is never encrypted). The encryptor is the ambient one from
    /// <see cref="SocigyFieldEncryption"/>; if none is configured, a clear error is thrown.
    /// </summary>
    public static class FieldCrypto
    {
        /// <summary>
        /// Encrypts <paramref name="value"/> (of declared type <paramref name="clrType"/>) to a ciphertext
        /// <c>byte[]</c>, or returns <see langword="null"/> for null. Pass <paramref name="associatedData"/>
        /// (e.g. <c>table:column</c>) to bind the ciphertext to its context; the same value must be supplied to
        /// <see cref="Decrypt"/>.
        /// </summary>
        public static object? Encrypt(object? value, Type clrType, byte[]? associatedData = null)
        {
            if (value == null || value is DBNull) return null;
            return SocigyFieldEncryption.Require().Encrypt(FieldValueCodec.Encode(value, clrType), associatedData);
        }

        /// <summary>Decrypts a <c>byte[]</c> read from the database back to the column's CLR value (boxed), or <see langword="null"/> for NULL.</summary>
        public static object? Decrypt(object? dbValue, Type clrType, byte[]? associatedData = null)
        {
            if (dbValue == null || dbValue is DBNull) return null;
            return FieldValueCodec.Decode(SocigyFieldEncryption.Require().Decrypt((byte[])dbValue, associatedData), clrType);
        }
    }
#nullable disable
}
