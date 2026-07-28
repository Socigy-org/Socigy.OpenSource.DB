using System.Linq;
using NUnit.Framework;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// The planner is where "this engine cannot do that" is resolved, so these tests drive it with synthetic
/// capability sets rather than a real generator. That is the point: they prove a second database engine is
/// accommodated correctly before a second engine exists.
///
/// The contract under test is the split between options that only cost performance (dropped, with a warning)
/// and options that change what the database enforces (refused outright).
/// </summary>
[TestFixture]
public class IndexPlannerTests
{
    private const int PostgreSql = 63;
    private const int MySql = 64;
    private const int SqlServer = 128;

    private static DbIndex Index(params string[] columns) => new()
    {
        TableName = "users",
        Columns = columns,
    };

    // Identity resolution: these tests are about the planner, not about column naming.
    private static IndexPlanner.IndexPlanResult Plan(
        DbIndex index, IndexCapabilities capabilities, int maxIdentifier = PostgreSql)
        => IndexPlanner.Plan(index, capabilities, p => p, maxIdentifier);

    // ── the happy path on a fully capable engine ──

    [Test]
    public void Full_capability_engine_keeps_every_option()
    {
        var index = Index("tenant_id", "created_at");
        index.IsUnique = true;
        index.Method = DbIndexMethods.Hash;
        index.Where = "deleted_at IS NULL";
        index.IncludeColumns = ["status"];
        index.DescendingColumns = ["created_at"];
        index.NullsLastColumns = ["created_at"];

        var result = Plan(index, IndexCapabilities.All);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Warnings, Is.Empty);
            Assert.That(result.Index!.IsUnique, Is.True);
            Assert.That(result.Index.Method, Is.EqualTo(DbIndexMethods.Hash));
            Assert.That(result.Index.Where, Is.EqualTo("deleted_at IS NULL"));
            Assert.That(result.Index.IncludeColumns, Is.EqualTo(new[] { "status" }));
            Assert.That(result.Index.Columns.Select(c => c.Name), Is.EqualTo(new[] { "tenant_id", "created_at" }),
                "key column order is part of what the index means and must be preserved");
            Assert.That(result.Index.Columns[1].Descending, Is.True);
            Assert.That(result.Index.Columns[1].Nulls, Is.EqualTo(DbIndexNulls.Last));
        });
    }

    // ── performance-only options degrade with a warning ──

    [Test]
    public void Unsupported_method_falls_back_to_the_default_with_a_warning()
    {
        var index = Index("bio");
        index.Method = DbIndexMethods.FullText;

        var result = Plan(index, IndexCapabilities.All & ~IndexCapabilities.FullText);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Index!.Method, Is.Null, "degrades to the engine's default index method");
            Assert.That(result.Warnings, Has.Some.Contains("full-text"));
        });
    }

    [Test]
    public void Unsupported_include_is_dropped_with_a_warning()
    {
        var index = Index("email");
        index.IncludeColumns = ["status", "tenant_id"];

        var result = Plan(index, IndexCapabilities.All & ~IndexCapabilities.Include);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Index!.IncludeColumns, Is.Empty);
            Assert.That(result.Warnings, Has.Some.Contains("covering columns"));
        });
    }

    [Test]
    public void Unsupported_ordering_is_dropped_with_a_warning()
    {
        var index = Index("created_at");
        index.DescendingColumns = ["created_at"];
        index.NullsFirstColumns = ["created_at"];

        var result = Plan(index,
            IndexCapabilities.All & ~IndexCapabilities.Descending & ~IndexCapabilities.NullsOrdering);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Index!.Columns[0].Descending, Is.False);
            Assert.That(result.Index.Columns[0].Nulls, Is.Null);
            Assert.That(result.Warnings, Has.Some.Contains("descending").And.Some.Contains("NULL ordering"));
        });
    }

    [Test]
    public void Unsupported_filter_on_a_plain_index_is_dropped_with_a_warning()
    {
        var index = Index("email");
        index.Where = "deleted_at IS NULL";

        var result = Plan(index, IndexCapabilities.All & ~IndexCapabilities.Partial);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Is.Empty, "a non-unique index over more rows still returns the same results");
            Assert.That(result.Index!.Where, Is.Null);
            Assert.That(result.Warnings, Has.Some.Contains("deleted_at IS NULL"));
        });
    }

    // ── options that change meaning are refused, not degraded ──

    [Test]
    public void Unsupported_filter_on_a_unique_index_is_an_error()
    {
        var index = Index("email");
        index.IsUnique = true;
        index.Where = "deleted_at IS NULL";

        var result = Plan(index, IndexCapabilities.All & ~IndexCapabilities.Partial);

        Assert.Multiple(() =>
        {
            Assert.That(result.Index, Is.Null, "no index may be emitted");
            Assert.That(result.Errors, Has.Some.Contains("uniqueness"),
                "indexing every row would enforce uniqueness over rows the filter excludes");
        });
    }

    [Test]
    public void Unsupported_uniqueness_is_an_error()
    {
        var index = Index("email");
        index.IsUnique = true;

        var result = Plan(index, IndexCapabilities.All & ~IndexCapabilities.Unique);

        Assert.Multiple(() =>
        {
            Assert.That(result.Index, Is.Null);
            Assert.That(result.Errors, Has.Some.Contains("unique"));
        });
    }

    [Test]
    public void Index_without_columns_is_an_error()
    {
        var result = Plan(new DbIndex { TableName = "users", Columns = [] }, IndexCapabilities.All);

        Assert.Multiple(() =>
        {
            Assert.That(result.Index, Is.Null);
            Assert.That(result.Errors, Has.Some.Contains("no columns"));
        });
    }

    // ── naming ──

    [Test]
    public void Explicit_name_is_kept()
    {
        var index = Index("email");
        index.Name = "my_index";

        Assert.That(Plan(index, IndexCapabilities.All).Index!.Name, Is.EqualTo("my_index"));
    }

    [Test]
    public void Derived_name_reflects_table_columns_and_uniqueness()
    {
        var plain = Plan(Index("tenant_id", "email"), IndexCapabilities.All).Index!;

        var unique = Index("tenant_id", "email");
        unique.IsUnique = true;

        Assert.Multiple(() =>
        {
            Assert.That(plain.Name, Is.EqualTo("IX_users_tenant_id_email"));
            Assert.That(Plan(unique, IndexCapabilities.All).Index!.Name, Is.EqualTo("UX_users_tenant_id_email"));
        });
    }

    [Test]
    public void Two_indexes_over_the_same_columns_get_different_names()
    {
        var plain = Index("email");
        var partial = Index("email");
        partial.Where = "deleted_at IS NULL";

        var plainName = Plan(plain, IndexCapabilities.All).Index!.Name;
        var partialName = Plan(partial, IndexCapabilities.All).Index!.Name;

        Assert.That(partialName, Is.Not.EqualTo(plainName),
            "an option-derived suffix keeps the second CREATE from colliding with the first");
    }

    [Test]
    public void Derived_name_ignores_options_the_engine_dropped()
    {
        // The same model on two engines, one of which cannot do covering columns. The index it does create
        // must carry the same name, or the two engines would disagree about what to DROP.
        var index = Index("email");
        index.IncludeColumns = ["status"];

        var withInclude = Plan(index, IndexCapabilities.All).Index!.Name;
        var withoutInclude = Plan(index, IndexCapabilities.All & ~IndexCapabilities.Include).Index!.Name;

        Assert.That(withoutInclude, Is.EqualTo("IX_users_email"));
        Assert.That(withInclude, Is.Not.EqualTo(withoutInclude));
    }

    [Test]
    public void Derived_name_is_reproducible()
    {
        var first = Plan(Index("tenant_id", "email"), IndexCapabilities.All).Index!.Name;
        var second = Plan(Index("tenant_id", "email"), IndexCapabilities.All).Index!.Name;

        Assert.That(second, Is.EqualTo(first),
            "a DOWN script's DROP has to match a name emitted by a different run of the tool");
    }

    [TestCase(PostgreSql)]
    [TestCase(MySql)]
    [TestCase(SqlServer)]
    public void Derived_name_fits_the_engines_identifier_limit(int limit)
    {
        var index = new DbIndex
        {
            TableName = new string('t', 40),
            Columns = [new string('a', 40), new string('b', 40)],
        };

        var name = Plan(index, IndexCapabilities.All, limit).Index!.Name;

        Assert.That(name.Length, Is.LessThanOrEqualTo(limit),
            "engines silently truncate over-long identifiers, which would collapse two names into one");
    }

    [Test]
    public void Truncated_names_stay_distinct()
    {
        var a = new DbIndex { TableName = new string('t', 60), Columns = ["column_alpha"] };
        var b = new DbIndex { TableName = new string('t', 60), Columns = ["column_bravo"] };

        var nameA = Plan(a, IndexCapabilities.All, PostgreSql).Index!.Name;
        var nameB = Plan(b, IndexCapabilities.All, PostgreSql).Index!.Name;

        Assert.That(nameA, Is.Not.EqualTo(nameB),
            "truncation must not merge two distinct indexes into one name");
    }
}
