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

    /// <summary>Computed (get-only) property — must be ignored by the generator (not treated as a column, which
    /// would emit a setter assignment that fails to compile).</summary>
    public string Display => $"{Name}#{Priority}";
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

    /// <summary>timestamptz column — used to exercise the JOIN aggregate (Max/Min) DateTimeOffset conversion.</summary>
    public DateTimeOffset CreatedTz { get; set; }

    /// <summary>A column whose snake_case name is 63 bytes, so the join output alias "a1_&lt;name&gt;" (66 bytes) exceeds
    /// PostgreSQL's 63-byte identifier limit. The old name-embedding join alias was truncated in the result label but
    /// not in the reader's lookup string, so this column silently read as NULL on a join. The positional alias fixes it.</summary>
    public string? LongJoinColumnNameUsedToExceedSixtyThreeAliasBoundary { get; set; }
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

    /// <summary>Non-enum byte/sbyte columns (stored as smallint). The default fast read path (ReadScalar) must
    /// narrow these from short — Npgsql has no int2->byte handler, so a direct GetFieldValue&lt;byte&gt; threw.</summary>
    public byte SmallByte { get; set; }
    public sbyte SignedByte { get; set; }

    /// <summary>A ulong-backed enum column. It is stored as NUMERIC (the ulong widening), which Npgsql returns as
    /// a boxed decimal; the fast ordinal read path passed that straight to Enum.ToObject, which rejects a decimal
    /// and threw. The row must still materialize via the default fast path.</summary>
    public BigStatus Big { get; set; }
}

/// <summary>ulong-backed enum (underlying stored as NUMERIC). High exceeds uint range, so it needs the full ulong.</summary>
public enum BigStatus : ulong { None = 0, Low = 5, High = 10000000000UL }

/// <summary>
/// Exercises a <c>char</c> column (stored as <c>character(1)</c>). Npgsql cannot bind/read a bare
/// <see cref="System.Char"/>, so the write paths must rebind it as a one-character string and the fast read path
/// must narrow a one-character string back — otherwise insert/update throw and the row fails to materialize.
/// </summary>
[Table("char_items")]
public partial class CharItem
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    public char Grade { get; set; }

    public char? Initial { get; set; }
}

/// <summary>ushort-backed enum (stored as INTEGER). High exceeds short range, so it needs the full ushort.</summary>
public enum WideShortStatus : ushort { None = 0, High = 40000 }

/// <summary>uint-backed enum (stored as BIGINT). High exceeds int range, so it needs the full uint.</summary>
public enum WideIntStatus : uint { None = 0, High = 3000000000U }

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

/// <summary>Base class carrying a shared column. A [Table] deriving from this must include the inherited
/// property as a real column (regression: inherited/other-partial properties were silently dropped).</summary>
public abstract class AuditableBase
{
    public string CreatedBy { get; set; } = "";
}

/// <summary>[Table] that inherits a column from <see cref="AuditableBase"/> and declares the rest across a second
/// partial declaration — exercises base-chain + multi-partial column discovery.</summary>
[Table("test_inherited")]
public partial class InheritedItem : AuditableBase
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    public string Name { get; set; } = "";
}

public partial class InheritedItem
{
    // Declared in a SEPARATE partial than the one carrying [Table]; must still become a column.
    public int Score { get; set; }
}

/// <summary>
/// Multi-constructor DTO: a narrow convenience constructor is declared FIRST, then the full one. The mapper
/// must bind through the WIDEST constructor (Name + Priority). Picking <c>InstanceConstructors[0]</c>
/// (declaration order) would take the 1-arg ctor and silently drop Priority — every row reads Priority == 0.
/// </summary>
public class ItemSummaryMulti
{
    public ItemSummaryMulti(string name) : this(name, 0) { }
    public ItemSummaryMulti(string name, int priority) { Name = name; Priority = priority; }
    public string Name { get; }
    public int Priority { get; }
}

/// <summary>
/// Property-bag DTO returned by a procedure (settable-property mapping). <see cref="Missing"/> has no
/// matching result column and must map to <c>default</c> via the safe-ordinal (-1) fallback.
/// </summary>
public enum ReportSeverity : byte { Low = 1, High = 200 }

public class ItemReport
{
    public string Name { get; set; } = "";
    public int Priority { get; set; }

    /// <summary>Plain byte from a smallint column — the DTO mapper must narrow short->byte, not GetFieldValue&lt;byte&gt;.</summary>
    public byte Rank { get; set; }

    /// <summary>byte-backed enum from a smallint column — same narrowing concern via the enum read path.</summary>
    public ReportSeverity Level { get; set; }

    /// <summary>Wide-unsigned-backed enums read by the DTO mapper. The enum branch read GetFieldValue&lt;ushort/uint/ulong&gt;
    /// directly over the widened integer/bigint/numeric storage, which Npgsql has no handler for, so it threw — while
    /// the non-enum unsigned cases in the same mapper narrow correctly. The mapper must narrow these too.</summary>
    public WideShortStatus WideShort { get; set; }
    public WideIntStatus WideInt { get; set; }
    public BigStatus WideLong { get; set; }

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

    /// <summary>Enum column — the baked CREATE TABLE (InstantiateAsync) must type it as the underlying integer,
    /// not text, so the insert (which binds the underlying integer) succeeds.</summary>
    public WorkStatus Status { get; set; }

    /// <summary>Auto-increment column with a CUSTOM sequence name — the baked CREATE TABLE must create that named
    /// sequence (not the serial default), so the runtime sequence accessor targets a sequence that exists.</summary>
    [AutoIncrement("audit_entry_custom_seq")]
    public long Counter { get; set; }
}

/// <summary>
/// Table with a <c>required</c> member — verifies the generated entity carries <c>[SetsRequiredMembers]</c> so
/// it satisfies the builders' <c>new()</c> constraint (issue #2).
/// </summary>
[Table("test_required")]
public partial class RequiredItem
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    public required string Label { get; set; }
}

/// <summary>
/// Table whose PRIMARY KEY has a <c>[ValueConvertor]</c>. The instance UPDATE/DELETE WHERE clause must bind the
/// converted PK value (regression: <c>GetPrimaryColumns</c> bound the raw value, so the WHERE matched no rows).
/// </summary>
[Table("test_convertor_pk_items")]
public partial class TestConvertorPkItem
{
    [PrimaryKey, ValueConvertor(typeof(UpperCaseStringConvertor))]
    public string Code { get; set; } = "";

    public string Note { get; set; } = "";
}

public enum WorkStatus { Pending, Active, Done }

/// <summary>
/// Stores an enum as its STRING name rather than its underlying integer. Exercises the UPDATE path's enum-coercion
/// guard (regression: it coerced by the declared enum type and crashed on the convertor's string output).
/// </summary>
public class WorkStatusStringConvertor : IDbValueConvertor<WorkStatus>
{
    public object? ConvertToDbValue(WorkStatus value) => value.ToString();
    public WorkStatus ConvertFromDbValue(object? dbValue)
        => dbValue == null || dbValue is DBNull ? default : Enum.Parse<WorkStatus>(dbValue.ToString()!);
}

/// <summary>Table with an enum column routed through a custom string-returning convertor.</summary>
[Table("test_enum_convertor_items")]
public partial class TestEnumConvertorItem
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    [ValueConvertor(typeof(WorkStatusStringConvertor))]
    public WorkStatus Status { get; set; }
}

/// <summary>
/// Table whose PRIMARY KEY is an enum routed through a TYPE-CHANGING convertor (enum→string). The DELETE-by-instance
/// WHERE clause must bind the converted string value and let Npgsql infer its type (regression: DELETE forced the
/// declared enum type → Integer onto the string value and threw, while UPDATE-by-instance succeeded).
/// </summary>
[Table("test_enum_pk_convertor_items")]
public partial class TestEnumPkConvertorItem
{
    [PrimaryKey, ValueConvertor(typeof(WorkStatusStringConvertor))]
    public WorkStatus Status { get; set; }

    public string Note { get; set; } = "";
}
