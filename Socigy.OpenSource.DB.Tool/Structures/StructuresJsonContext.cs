using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Socigy.OpenSource.DB.Tool.Structures
{
    [JsonSerializable(typeof(SocigySettings))]
    [JsonSerializable(typeof(DbSchema))]
    [JsonSerializable(typeof(SchemaDiff))]

    // ------- ENUMS -------
    [JsonSerializable(typeof(byte))]
    [JsonSerializable(typeof(short))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(long))]
    [JsonSerializable(typeof(double))]
    [JsonSerializable(typeof(float))]
    [JsonSerializable(typeof(decimal))]

    [JsonSerializable(typeof(sbyte))]
    [JsonSerializable(typeof(ushort))]
    [JsonSerializable(typeof(uint))]
    [JsonSerializable(typeof(ulong))]

    [JsonSerializable(typeof(IntPtr))]
    [JsonSerializable(typeof(UIntPtr))]
    [JsonSerializable(typeof(char))]
    // ------- ENUMS -------
    public partial class StructuresJsonContext : JsonSerializerContext
    {
    }
}
