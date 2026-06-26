using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Core.Convertors;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UnitTest.DB;

// ---------------------------------------------------------------------------
// Test models used by the unit test suite.
// Each partial class gets augmented by the Socigy source generator.
// ---------------------------------------------------------------------------

/// <summary>Enum whose values live in their own table, used for N:M junction tests.</summary>
[Flags]
[Table("test_roles")]
public enum TestRole
{
    Reader = 1,
    Writer = 2,
    Moderator = 4,
    Admin = 8
}

/// <summary>
/// Basic CRUD table.  <c>Id</c> and <c>CreatedAt</c> have DB-side defaults so
/// that we can exercise <c>ExcludeAutoFields()</c> + <c>WithValuePropagation()</c>.
/// </summary>
[Table("test_items")]
public partial class TestItem
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public int Priority { get; set; }

    [Default(DbDefaults.Time.Now)]
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Table with an <see cref="AutoIncrementAttribute"/> column — exercises
/// <c>SeqSequence.GetNextValueAsync</c> / <c>PeekCurrentValueAsync</c>.
/// </summary>
[Table("test_counters")]
public partial class TestCounter
{
    [PrimaryKey]
    public Guid Id { get; set; }

    [AutoIncrement]
    public int Seq { get; set; }

    public string Label { get; set; } = "";
}

// ---------------------------------------------------------------------------
// JSON column test models
// ---------------------------------------------------------------------------

/// <summary>POCO stored as a typed <c>jsonb</c> column via <c>[JsonColumn]</c>.</summary>
public class TestJsonPayload
{
    public string Title { get; set; } = "";
    public int Score { get; set; }
    public List<string> Tags { get; set; } = [];
}

/// <summary>AOT-safe <see cref="JsonSerializerContext"/> for <see cref="TestJsonPayload"/>.</summary>
[JsonSerializable(typeof(TestJsonPayload))]
public partial class TestJsonContext : JsonSerializerContext { }

/// <summary>
/// Table with one raw-JSON column (<c>[RawJsonColumn]</c>) and one typed-JSON
/// column (<c>[JsonColumn]</c>) — exercises jsonb insert, query, and update.
/// </summary>
[Table("test_json_items")]
public partial class TestJsonItem
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Stored verbatim as <c>jsonb</c>; any valid JSON string is accepted.</summary>
    [RawJsonColumn]
    public string? RawData { get; set; }

    /// <summary>Serialized/deserialized via <see cref="TestJsonContext"/> (AOT-safe).</summary>
    [JsonColumn(typeof(TestJsonContext))]
    public TestJsonPayload? Payload { get; set; }
}

// ---------------------------------------------------------------------------
// Value convertor test models
// ---------------------------------------------------------------------------

/// <summary>
/// Converts a <c>string</c> to upper-case before writing to the DB and returns
/// it as-is when reading back.
/// </summary>
public class UpperCaseStringConvertor : IDbValueConvertor<string>
{
    public object? ConvertToDbValue(string? value) => value?.ToUpperInvariant();
    public string? ConvertFromDbValue(object? dbValue) => dbValue?.ToString();
}

/// <summary>Table that exercises <c>[ValueConvertor]</c> on a <c>string</c> column.</summary>
[Table("test_convertor_items")]
public partial class TestConvertorItem
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    /// <summary>Stored as upper-case in the DB via <see cref="UpperCaseStringConvertor"/>.</summary>
    [ValueConvertor(typeof(UpperCaseStringConvertor))]
    public string Label { get; set; } = "";

    public int Value { get; set; }
}

/// <summary>
/// Table with a <see cref="FlaggedEnumAttribute"/> property — exercises the
/// auto-generated junction-table methods (Insert/Delete/Get/Has/Sync) and
/// the <c>HasFlag</c> WHERE translation.
/// </summary>
[Table("test_users")]
public partial class TestUser
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    public string Username { get; set; } = "";

    [FlaggedEnum]
    public TestRole Role { get; set; }
}

/// <summary>
/// Table exercising a broader set of CLR types and a nullable column — drives the
/// type-binding and parser-operator (bool / Nullable&lt;T&gt; / arithmetic / coalesce) tests.
/// </summary>
[Table("test_types")]
public partial class TestType
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    public bool IsActive { get; set; }

    public int? NullableValue { get; set; }

    public decimal Amount { get; set; }

    [Default(DbDefaults.Time.Now)]
    public DateTime When { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// Table exercising <c>[Encrypted]</c> columns across several CLR types. Each encrypted column is stored
/// as <c>bytea</c> ciphertext and round-trips through the ambient <c>IFieldEncryptor</c>.
/// </summary>
[Table("test_secrets")]
public partial class TestSecret
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    /// <summary>Plain (unencrypted) lookup key — encrypted columns can't be queried.</summary>
    public string Owner { get; set; } = "";

    [Encrypted]
    public string Ssn { get; set; } = "";

    [Encrypted]
    public int Pin { get; set; }

    [Encrypted]
    public Guid Token { get; set; }

    [Encrypted]
    public DateTime IssuedAt { get; set; }

    [Encrypted]
    public string? Note { get; set; }

    /// <summary>Encrypted but NOT auto-decrypted — exposes <c>ManualRawEncrypted</c> + lazy <c>ManualDecrypted</c>.</summary>
    [Encrypted(AutoDecrypt = false)]
    public string Manual { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Procedure DTO return types (deliberately NOT [Table]) — exercise the
// generator-emitted, AOT-safe DTO mappers used by `-- @returns: <non-table>`.
// ---------------------------------------------------------------------------

/// <summary>Positional record DTO returned by a procedure (constructor-bound mapping).</summary>
public record ItemSummary(string Name, int Priority);

/// <summary>
/// Property-bag DTO returned by a procedure (settable-property mapping). <see cref="Missing"/> has no
/// matching result column and must map to <c>default</c> via the safe-ordinal (-1) fallback.
/// </summary>
public class ItemReport
{
    public string Name { get; set; } = "";
    public int Priority { get; set; }
    public string? Missing { get; set; }
}

/// <summary>
/// Runtime-named typed table (<c>[TableType]</c>) — exercises <c>DynamicTable&lt;T&gt;</c>: the column shape is
/// fixed here, but the table name is bound at runtime via <c>WithTableName</c>/<c>MapTypeAsync</c>.
/// </summary>
[TableType]
public partial class AuditEntry
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Action { get; set; } = "";

    public int Amount { get; set; }

    public DateTime At { get; set; }
}
