using System;
using System.Text;

namespace Socigy.OpenSource.DB.Core.Encryption
{
#nullable enable
    /// <summary>
    /// Converts a column's CLR value to and from a compact <see cref="byte"/>[] so it can be encrypted and
    /// stored as <c>bytea</c>. The CLR type is known at both ends (the generator passes <c>typeof(T)</c>),
    /// so the encoding carries no type tag. Reflection-free and AOT-safe — an explicit type switch only.
    /// <para>
    /// Supported: <see cref="bool"/>, all integer / floating / <see cref="decimal"/> types, <see cref="char"/>,
    /// <see cref="string"/> (UTF-8), <see cref="Guid"/>, <see cref="DateTime"/>, <see cref="DateTimeOffset"/>,
    /// <see cref="TimeSpan"/>, <see cref="byte"/>[], any <c>enum</c> (its underlying type), and <see cref="Nullable{T}"/>
    /// of each. <c>null</c> is never encrypted — callers keep it as SQL <c>NULL</c>. Unsupported types throw
    /// <see cref="NotSupportedException"/>.
    /// </para>
    /// <para>
    /// All multi-byte numbers are encoded little-endian regardless of the host architecture, so ciphertext
    /// written on one machine always decodes correctly on another (the on-disk format is fixed, not
    /// machine-dependent).
    /// </para>
    /// </summary>
    public static class FieldValueCodec
    {
        /// <summary>Encodes a non-null column value to bytes for encryption. <paramref name="clrType"/> is the column's declared type.</summary>
        public static byte[] Encode(object value, Type clrType)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            Type t = Normalize(clrType);

            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean: return new[] { (byte)((bool)value ? 1 : 0) };
                case TypeCode.Byte: return new[] { (byte)value };
                case TypeCode.SByte: return new[] { unchecked((byte)(sbyte)value) };
                case TypeCode.Int16: return ToLE(BitConverter.GetBytes((short)value));
                case TypeCode.UInt16: return ToLE(BitConverter.GetBytes((ushort)value));
                case TypeCode.Int32: return ToLE(BitConverter.GetBytes((int)value));
                case TypeCode.UInt32: return ToLE(BitConverter.GetBytes((uint)value));
                case TypeCode.Int64: return ToLE(BitConverter.GetBytes((long)value));
                case TypeCode.UInt64: return ToLE(BitConverter.GetBytes((ulong)value));
                case TypeCode.Single: return ToLE(BitConverter.GetBytes((float)value));
                case TypeCode.Double: return ToLE(BitConverter.GetBytes((double)value));
                case TypeCode.Char: return ToLE(BitConverter.GetBytes((char)value));
                case TypeCode.String: return Encoding.UTF8.GetBytes((string)value);
                case TypeCode.Decimal: return EncodeDecimal((decimal)value);
                case TypeCode.DateTime: return EncodeDateTime((DateTime)value);
            }

            if (t == typeof(Guid)) return ((Guid)value).ToByteArray();
            if (t == typeof(byte[])) return (byte[])value;
            if (t == typeof(TimeSpan)) return ToLE(BitConverter.GetBytes(((TimeSpan)value).Ticks));
            if (t == typeof(DateTimeOffset))
            {
                var dto = (DateTimeOffset)value;
                var buf = new byte[16];
                Buffer.BlockCopy(ToLE(BitConverter.GetBytes(dto.Ticks)), 0, buf, 0, 8);
                Buffer.BlockCopy(ToLE(BitConverter.GetBytes(dto.Offset.Ticks)), 0, buf, 8, 8);
                return buf;
            }

            throw new NotSupportedException(
                $"Encrypting a column of type '{clrType}' is not supported. Supported types are the common " +
                "primitives, decimal, string, Guid, DateTime, DateTimeOffset, TimeSpan, byte[], enums, and Nullable<> of these.");
        }

        /// <summary>Decodes bytes produced by <see cref="Encode"/> back to the column's CLR value. Returns the underlying value (caller boxes/casts).</summary>
        public static object Decode(byte[] bytes, Type clrType)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            Type t = Normalize(clrType);
            object result;

            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean: result = bytes[0] != 0; break;
                case TypeCode.Byte: result = bytes[0]; break;
                case TypeCode.SByte: result = unchecked((sbyte)bytes[0]); break;
                case TypeCode.Int16: result = BitConverter.ToInt16(FromLE(bytes, 0, 2), 0); break;
                case TypeCode.UInt16: result = BitConverter.ToUInt16(FromLE(bytes, 0, 2), 0); break;
                case TypeCode.Int32: result = BitConverter.ToInt32(FromLE(bytes, 0, 4), 0); break;
                case TypeCode.UInt32: result = BitConverter.ToUInt32(FromLE(bytes, 0, 4), 0); break;
                case TypeCode.Int64: result = BitConverter.ToInt64(FromLE(bytes, 0, 8), 0); break;
                case TypeCode.UInt64: result = BitConverter.ToUInt64(FromLE(bytes, 0, 8), 0); break;
                case TypeCode.Single: result = BitConverter.ToSingle(FromLE(bytes, 0, 4), 0); break;
                case TypeCode.Double: result = BitConverter.ToDouble(FromLE(bytes, 0, 8), 0); break;
                case TypeCode.Char: result = BitConverter.ToChar(FromLE(bytes, 0, 2), 0); break;
                case TypeCode.String: return Encoding.UTF8.GetString(bytes);
                case TypeCode.Decimal: return DecodeDecimal(bytes);
                case TypeCode.DateTime: return DecodeDateTime(bytes);
                default: result = DecodeNonTypeCode(bytes, t, clrType); break;
            }

            // Re-materialize enums from their underlying value so the boxed type matches the column.
            Type declared = Nullable.GetUnderlyingType(clrType) ?? clrType;
            if (declared.IsEnum)
                return Enum.ToObject(declared, result);
            return result;
        }

        private static object DecodeNonTypeCode(byte[] bytes, Type t, Type clrType)
        {
            if (t == typeof(Guid)) return new Guid(bytes);
            if (t == typeof(byte[])) return bytes;
            if (t == typeof(TimeSpan)) return new TimeSpan(BitConverter.ToInt64(FromLE(bytes, 0, 8), 0));
            if (t == typeof(DateTimeOffset))
            {
                long ticks = BitConverter.ToInt64(FromLE(bytes, 0, 8), 0);
                long offsetTicks = BitConverter.ToInt64(FromLE(bytes, 8, 8), 0);
                return new DateTimeOffset(ticks, new TimeSpan(offsetTicks));
            }
            throw new NotSupportedException($"Decrypting a column of type '{clrType}' is not supported.");
        }

        private static Type Normalize(Type clrType)
        {
            Type t = Nullable.GetUnderlyingType(clrType) ?? clrType;
            if (t.IsEnum) t = Enum.GetUnderlyingType(t);
            return t;
        }

        private static byte[] EncodeDecimal(decimal value)
        {
            int[] bits = decimal.GetBits(value);
            var buf = new byte[16];
            for (int i = 0; i < 4; i++)
                Buffer.BlockCopy(ToLE(BitConverter.GetBytes(bits[i])), 0, buf, i * 4, 4);
            return buf;
        }

        private static decimal DecodeDecimal(byte[] bytes)
        {
            var bits = new int[4];
            for (int i = 0; i < 4; i++)
                bits[i] = BitConverter.ToInt32(FromLE(bytes, i * 4, 4), 0);
            return new decimal(bits);
        }

        private static byte[] EncodeDateTime(DateTime value)
        {
            var buf = new byte[9];
            Buffer.BlockCopy(ToLE(BitConverter.GetBytes(value.Ticks)), 0, buf, 0, 8);
            buf[8] = (byte)value.Kind;
            return buf;
        }

        private static DateTime DecodeDateTime(byte[] bytes)
        {
            long ticks = BitConverter.ToInt64(FromLE(bytes, 0, 8), 0);
            var kind = (DateTimeKind)bytes[8];
            return new DateTime(ticks, kind);
        }

        // BitConverter uses the host byte order; these pin the stored bytes to little-endian so the format
        // is portable. On the common little-endian hosts they are no-ops.
        private static byte[] ToLE(byte[] hostOrdered)
        {
            if (!BitConverter.IsLittleEndian) Array.Reverse(hostOrdered);
            return hostOrdered;
        }

        private static byte[] FromLE(byte[] littleEndian, int offset, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(littleEndian, offset, slice, 0, count);
            if (!BitConverter.IsLittleEndian) Array.Reverse(slice);
            return slice;
        }
    }
#nullable disable
}
