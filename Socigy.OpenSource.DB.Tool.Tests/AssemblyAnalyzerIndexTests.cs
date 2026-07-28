using System.Linq;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Reading <c>[Index]</c> off a compiled model. This goes through the real reflection-based analysis under
/// MetadataLoadContext, which is the only way attribute reading is actually exercised.
/// </summary>
[TestFixture]
public class AssemblyAnalyzerIndexTests
{
    private static DbTable AnalyzeTable(string classAttributes, string properties)
    {
        string model = $@"
using System;
using Socigy.OpenSource.DB.Attributes;
namespace Fixture
{{
    [Table(""users"")]
    {classAttributes}
    public partial class User
    {{
        [PrimaryKey] public Guid Id {{ get; set; }}
        {properties}
    }}
}}";
        return AnalyzerModelCompiler.Analyze(model).Tables.First(t => t.Name == "users");
    }

    private static DbIndex Single(DbTable table)
    {
        Assert.That(table.Indexes, Is.Not.Null.And.Count.EqualTo(1));
        return table.Indexes[0];
    }

    [Test]
    public void Property_level_index_covers_that_property()
    {
        var index = Single(AnalyzeTable("", "[Index] public string Email { get; set; }"));

        Assert.Multiple(() =>
        {
            // Property names, not column names: the generator resolves them, exactly as it does for [Unique].
            Assert.That(index.Columns, Is.EqualTo(new[] { "Email" }));
            Assert.That(index.TableName, Is.EqualTo("users"));
            Assert.That(index.IsUnique, Is.False);
        });
    }

    [Test]
    public void Class_level_index_is_composite_and_keeps_column_order()
    {
        var index = Single(AnalyzeTable(
            "[Index(nameof(TenantId), nameof(Email))]",
            "public Guid TenantId { get; set; } public string Email { get; set; }"));

        Assert.That(index.Columns, Is.EqualTo(new[] { "TenantId", "Email" }),
            "an index on (a, b) does not serve the same queries as one on (b, a)");
    }

    // [Index] is AllowMultiple: a column commonly carries a plain index plus a partial one.
    [Test]
    public void Several_indexes_on_one_property_are_all_read()
    {
        var table = AnalyzeTable("",
            @"[Index]
              [Index(Where = ""deleted_at IS NULL"", Name = ""ix_live"")]
              public string Email { get; set; }");

        Assert.Multiple(() =>
        {
            Assert.That(table.Indexes, Has.Count.EqualTo(2));
            Assert.That(table.Indexes.Select(i => i.Where), Has.Some.EqualTo("deleted_at IS NULL"));
            Assert.That(table.Indexes.Select(i => i.Name), Has.Some.EqualTo("ix_live"));
        });
    }

    [Test]
    public void Several_class_level_indexes_are_all_read()
    {
        var table = AnalyzeTable(
            @"[Index(nameof(TenantId), nameof(Email), Unique = true)]
              [Index(nameof(Email))]",
            "public Guid TenantId { get; set; } public string Email { get; set; }");

        Assert.That(table.Indexes, Has.Count.EqualTo(2));
    }

    [Test]
    public void Every_named_argument_lands_on_the_index()
    {
        var index = Single(AnalyzeTable(
            @"[Index(nameof(TenantId), nameof(Email),
                     Name = ""ix_custom"",
                     Unique = true,
                     Method = DbIndexMethods.Hash,
                     RawMethod = ""gist"",
                     Where = ""archived = false"",
                     Include = new[] { nameof(Status) })]",
            @"public Guid TenantId { get; set; }
              public string Email { get; set; }
              public string Status { get; set; }"));

        Assert.Multiple(() =>
        {
            Assert.That(index.Name, Is.EqualTo("ix_custom"));
            Assert.That(index.IsUnique, Is.True);
            Assert.That(index.Method, Is.EqualTo(DbIndexMethods.Hash));
            Assert.That(index.RawMethod, Is.EqualTo("gist"));
            Assert.That(index.Where, Is.EqualTo("archived = false"));
            Assert.That(index.IncludeColumns, Is.EqualTo(new[] { "Status" }));
        });
    }

    // The attribute accepts ordering as a scalar covering every key column or as arrays naming individual
    // ones; both must arrive downstream in the single per-column representation.
    [Test]
    public void Scalar_ordering_is_expanded_over_every_key_column()
    {
        var index = Single(AnalyzeTable(
            @"[Index(nameof(TenantId), nameof(Email), Descending = true, Nulls = DbIndexNulls.First)]",
            "public Guid TenantId { get; set; } public string Email { get; set; }"));

        Assert.Multiple(() =>
        {
            Assert.That(index.DescendingColumns, Is.EquivalentTo(new[] { "TenantId", "Email" }));
            Assert.That(index.NullsFirstColumns, Is.EquivalentTo(new[] { "TenantId", "Email" }));
            Assert.That(index.NullsLastColumns, Is.Null.Or.Empty);
        });
    }

    [Test]
    public void Per_column_ordering_overrides_the_scalar()
    {
        var index = Single(AnalyzeTable(
            @"[Index(nameof(TenantId), nameof(Email),
                     Nulls = DbIndexNulls.First,
                     DescendingColumns = new[] { nameof(Email) },
                     NullsLastColumns = new[] { nameof(Email) })]",
            "public Guid TenantId { get; set; } public string Email { get; set; }"));

        Assert.Multiple(() =>
        {
            Assert.That(index.DescendingColumns, Is.EqualTo(new[] { "Email" }),
                "only the named column is descending");
            Assert.That(index.NullsFirstColumns, Is.EqualTo(new[] { "TenantId" }),
                "the scalar still covers the columns the arrays do not name");
            Assert.That(index.NullsLastColumns, Is.EqualTo(new[] { "Email" }),
                "and the array wins for the column it does name");
        });
    }

    [Test]
    public void A_table_without_indexes_has_none()
    {
        Assert.That(AnalyzeTable("", "public string Email { get; set; }").Indexes, Is.Null.Or.Empty);
    }
}
