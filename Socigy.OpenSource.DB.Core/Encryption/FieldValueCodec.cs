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
                case TypeCode.Int16: return BitConverter.GetBytes((short)value);
                case TypeCode.UInt16: return BitConverter.GetBytes((ushort)value);
                case TypeCode.Int32: return BitConverter.GetBytes((int)value);
                case TypeCode.UInt32: return BitConverter.GetBytes((uint)value);
                case TypeCode.Int64: return BitConverter.GetBytes((long)value);
                case TypeCode.UInt64: return BitConverter.GetBytes((ulong)value);
                case TypeCode.Single: return BitConverter.GetBytes((float)value);
                case TypeCode.Double: return BitConverter.GetBytes((double)value);
                case TypeCode.Char: return BitConverter.GetBytes((char)value);
                case TypeCode.String: return Encoding.UTF8.GetBytes((string)value);
                case TypeCode.Decimal: return EncodeDecimal((decimal)value);
                case TypeCode.DateTime: return EncodeDateTime((DateTime)value);
            }

            if (t == typeof(Guid)) return ((Guid)value).ToByteArray();
            if (t == typeof(byte[])) return (byte[])value;
            if (t == typeof(TimeSpan)) return BitConverter.GetBytes(((TimeSpan)value).Ticks);
            if (t == typeof(DateTimeOffset))
            {
                var dto = (DateTimeOffset)value;
                var buf = new byte[16];
                Buffer.BlockCopy(BitConverter.GetBytes(dto.Ticks), 0, buf, 0, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(dto.Offset.Ticks), 0, buf, 8, 8);
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
                case TypeCode.Int16: result = BitConverter.ToInt16(bytes, 0); break;
                case TypeCode.UInt16: result = BitConverter.ToUInt16(bytes, 0); break;
                case TypeCode.Int32: result = BitConverter.ToInt32(bytes, 0); break;
                case TypeCode.UInt32: result = BitConverter.ToUInt32(bytes, 0); break;
                case TypeCode.Int64: result = BitConverter.ToInt64(bytes, 0); break;
                case TypeCode.UInt64: result = BitConverter.ToUInt64(bytes, 0); break;
                case TypeCode.Single: result = BitConverter.ToSingle(bytes, 0); break;
                case TypeCode.Double: result = BitConverter.ToDouble(bytes, 0); break;
                case TypeCode.Char: result = BitConverter.ToChar(bytes, 0); break;
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
            if (t == typeof(TimeSpan)) return new TimeSpan(BitConverter.ToInt64(bytes, 0));
            if (t == typeof(DateTimeOffset))
            {
                long ticks = BitConverter.ToInt64(bytes, 0);
                long offsetTicks = BitConverter.ToInt64(bytes, 8);
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
                Buffer.BlockCopy(BitConverter.GetBytes(bits[i]), 0, buf, i * 4, 4);
            return buf;
        }

        private static decimal DecodeDecimal(byte[] bytes)
        {
            var bits = new int[4];
            for (int i = 0; i < 4; i++)
                bits[i] = BitConverter.ToInt32(bytes, i * 4);
            return new decimal(bits);
        }

        private static byte[] EncodeDateTime(DateTime value)
        {
            var buf = new byte[9];
            Buffer.BlockCopy(BitConverter.GetBytes(value.Ticks), 0, buf, 0, 8);
            buf[8] = (byte)value.Kind;
            return buf;
        }

        private static DateTime DecodeDateTime(byte[] bytes)
        {
            long ticks = BitConverter.ToInt64(bytes, 0);
            var kind = (DateTimeKind)bytes[8];
            return new DateTime(ticks, kind);
        }
    }
#nullable disable
}
