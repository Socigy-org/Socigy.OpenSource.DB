using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Socigy.OpenSource.DB.Core.CommandBuilders;
using UnitTest.DB;
using System.Data.Common;
using Socigy.OpenSource.DB.Core;
using Socigy.OpenSource.DB.Core.Context;
using Socigy.OpenSource.DB.TestDb.Context;
using Bulk = Socigy.OpenSource.DB.Core.Bulk;

namespace UnitTest.DB.Tests;

/// <summary>
/// <see cref="InsertFields.ServerDefaultsWhenUnset"/> asserted on the value that actually lands in the
/// database, on every path that takes both a <c>fields</c> option and a <c>keep</c> list.
///
/// <para>
/// These assert the <b>outcome</b>, not the call. That distinction is the whole reason this fixture exists:
/// the option can be accepted, compile, run, and be silently discarded, in which case a test that only checks
/// "the insert succeeded" passes while the row holds the database default instead of the caller's value. The
/// only thing that settles it is reading the column back.
/// </para>
/// <para>
/// <c>test_items.created_at</c> is <c>[Default(DbDefaults.Time.Now)]</c> and <c>test_items.id</c> is
/// <c>[Default(DbDefaults.Guid.Random)]</c>, so a value the test assigns to either is distinguishable from
/// what the server would have written.
/// </para>
/// </summary>
[TestFixture]
public class ServerDefaultsWhenUnsetTests : BaseUnitTest
{
    /// <summary>A time the server default cannot produce, so "the caller's value survived" is unambiguous.</summary>
    private static readonly DateTime Assigned = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Unspecified);

    private async Task<TestItem> ReadByNameAsync(string name)
        => (await TestItem.Query(x => x.Name == name).WithConnection(Connection).ExecuteAsync().ToListAsync()).Single();

    private static string NewName(string tag) => $"whenunset-{tag}-{Guid.NewGuid():N}";

    /// <summary>Minimal connection factory that hands the context a fresh test connection.</summary>
    private sealed class TestConnectionFactory : IDbConnectionFactory
    {
        public DbConnection Create(string? connectionKey = null) => UnitCore.CreateConnection();
        public Task<bool> EnsureDbExists() => Task.FromResult(true);
    }

    /// <summary>Runs one context operation, matching how application code reaches these methods.</summary>
    private static Task WithContext(Func<ITestDb, Task> body)
        => new TestDbFactory(new TestConnectionFactory(), new SocigyDbContextOptions()).ExecuteAsync(body);

    private static void AssertCallerValueSurvived(TestItem row, Guid id, string because)
        => Assert.Multiple(() =>
        {
            Assert.That(row.CreatedAt, Is.EqualTo(Assigned), because);
            Assert.That(row.Id, Is.EqualTo(id), because);
        });

    // ── the option alone ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Context_single_writes_the_assigned_value()
    {
        var (name, id) = (NewName("ctx"), Guid.NewGuid());
        await WithContext(db => db.TestItems.InsertAsync(
            new TestItem { Id = id, Name = name, Priority = 1, CreatedAt = Assigned },
            InsertFields.ServerDefaultsWhenUnset));

        AssertCallerValueSurvived(await ReadByNameAsync(name), id, "the row carries values, so nothing is omitted");
    }

    [Test]
    public async Task Context_single_still_lets_the_server_fill_what_the_row_leaves_unset()
    {
        var name = NewName("ctx-unset");
        await WithContext(db => db.TestItems.InsertAsync(
            new TestItem { Name = name, Priority = 1 },   // Id and CreatedAt at their CLR defaults
            InsertFields.ServerDefaultsWhenUnset));

        var row = await ReadByNameAsync(name);
        Assert.Multiple(() =>
        {
            Assert.That(row.Id, Is.Not.EqualTo(Guid.Empty), "unset -> server gen_random_uuid()");
            Assert.That(row.CreatedAt.Year, Is.GreaterThanOrEqualTo(2025), "unset -> server NOW()");
        });
    }

    [Test]
    public async Task Static_multiple_writes_the_assigned_value()
    {
        var (name, id) = (NewName("static"), Guid.NewGuid());
        await TestItem.InsertMultipleAsync(
            new[] { new TestItem { Id = id, Name = name, Priority = 2, CreatedAt = Assigned } },
            Connection, fields: InsertFields.ServerDefaultsWhenUnset);

        AssertCallerValueSurvived(await ReadByNameAsync(name), id, "batch path");
    }

    [Test]
    public async Task BulkCopy_writes_the_assigned_value()
    {
        var (name, id) = (NewName("copy"), Guid.NewGuid());
        await Bulk.BulkCopy.InsertMultipleCopyAsync(
            new[] { new TestItem { Id = id, Name = name, Priority = 3, CreatedAt = Assigned } },
            Connection, fields: InsertFields.ServerDefaultsWhenUnset);

        AssertCallerValueSurvived(await ReadByNameAsync(name), id, "COPY path");
    }

    [Test]
    public async Task Fluent_builder_writes_the_assigned_value()
    {
        var (name, id) = (NewName("fluent"), Guid.NewGuid());
        await new TestItem { Id = id, Name = name, Priority = 4, CreatedAt = Assigned }
            .Insert().ExcludeAutoFieldsWhenUnset().WithConnection(Connection).ExecuteAsync();

        AssertCallerValueSurvived(await ReadByNameAsync(name), id, "fluent builder");
    }

    // ── the option COMBINED WITH keep ──────────────────────────────────────────────────────────────────
    //
    // A keep list is present at the overwhelming majority of real call sites, so "works without keep" is not
    // evidence the option works. If a path lets keep override the mode, ServerDefaultsWhenUnset silently
    // degrades to ServerDefaults and the caller's value is replaced by the database default — with the insert
    // succeeding and nothing logged.

    [Test]
    public async Task Context_single_with_keep_writes_the_assigned_value()
    {
        var (name, id) = (NewName("ctx-keep"), Guid.NewGuid());
        await WithContext(db => db.TestItems.InsertAsync(
            new TestItem { Id = id, Name = name, Priority = 5, CreatedAt = Assigned },
            InsertFields.ServerDefaultsWhenUnset,
            keep: r => new object?[] { r.Id }));

        AssertCallerValueSurvived(await ReadByNameAsync(name), id,
            "keep names Id; CreatedAt is set, so ServerDefaultsWhenUnset must still write it");
    }

    [Test]
    public async Task Context_multiple_with_keep_writes_the_assigned_value()
    {
        var (name, id) = (NewName("ctx-multi-keep"), Guid.NewGuid());
        await WithContext(db => db.TestItems.InsertMultipleAsync(
            new[] { new TestItem { Id = id, Name = name, Priority = 6, CreatedAt = Assigned } },
            InsertFields.ServerDefaultsWhenUnset,
            keep: r => new object?[] { r.Id }));

        AssertCallerValueSurvived(await ReadByNameAsync(name), id, "context batch path with keep");
    }

    [Test]
    public async Task Static_multiple_with_keep_writes_the_assigned_value()
    {
        var (name, id) = (NewName("static-keep"), Guid.NewGuid());
        await TestItem.InsertMultipleAsync(
            new[] { new TestItem { Id = id, Name = name, Priority = 7, CreatedAt = Assigned } },
            Connection, fields: InsertFields.ServerDefaultsWhenUnset,
            keep: r => new object?[] { r.Id });

        AssertCallerValueSurvived(await ReadByNameAsync(name), id, "static batch path with keep");
    }

    [Test]
    public async Task BulkCopy_with_keep_writes_the_assigned_value()
    {
        var (name, id) = (NewName("copy-keep"), Guid.NewGuid());
        await Bulk.BulkCopy.InsertMultipleCopyAsync(
            new[] { new TestItem { Id = id, Name = name, Priority = 8, CreatedAt = Assigned } },
            Connection, fields: InsertFields.ServerDefaultsWhenUnset,
            keep: r => new object?[] { r.Id });

        AssertCallerValueSurvived(await ReadByNameAsync(name), id, "COPY path with keep");
    }

    [Test]
    public async Task Fluent_builder_with_keep_writes_the_assigned_value()
    {
        var (name, id) = (NewName("fluent-keep"), Guid.NewGuid());
        await new TestItem { Id = id, Name = name, Priority = 9, CreatedAt = Assigned }
            .Insert().ExcludeAutoFieldsWhenUnset(nameof(TestItem.Id))
            .WithConnection(Connection).ExecuteAsync();

        AssertCallerValueSurvived(await ReadByNameAsync(name), id, "fluent builder with an include list");
    }

    // ── the AOT-safe string[] spelling must be able to express the mode at all ─────────────────────────

    [Test]
    public async Task Context_single_with_string_keep_writes_the_assigned_value()
    {
        var (name, id) = (NewName("ctx-strkeep"), Guid.NewGuid());
        await WithContext(db => db.TestItems.InsertAsync(
            new TestItem { Id = id, Name = name, Priority = 10, CreatedAt = Assigned },
            new[] { nameof(TestItem.Id) },
            InsertFields.ServerDefaultsWhenUnset));

        AssertCallerValueSurvived(await ReadByNameAsync(name), id, "AOT-safe string[] overload");
    }

    [Test]
    public async Task Static_multiple_with_string_keep_writes_the_assigned_value()
    {
        var (name, id) = (NewName("static-strkeep"), Guid.NewGuid());
        await TestItem.InsertMultipleAsync(
            new[] { new TestItem { Id = id, Name = name, Priority = 11, CreatedAt = Assigned } },
            Connection, new[] { nameof(TestItem.Id) },
            fields: InsertFields.ServerDefaultsWhenUnset);

        AssertCallerValueSurvived(await ReadByNameAsync(name), id, "AOT-safe static batch overload");
    }

    // ── plain ServerDefaults must keep behaving exactly as before ─────────────────────────────────────

    [Test]
    public async Task ServerDefaults_with_keep_still_lets_the_server_win_for_unnamed_columns()
    {
        var (name, id) = (NewName("plain"), Guid.NewGuid());
        await WithContext(db => db.TestItems.InsertAsync(
            new TestItem { Id = id, Name = name, Priority = 12, CreatedAt = Assigned },
            InsertFields.ServerDefaults,
            keep: r => new object?[] { r.Id }));

        var row = await ReadByNameAsync(name);
        Assert.Multiple(() =>
        {
            Assert.That(row.Id, Is.EqualTo(id), "named in keep");
            Assert.That(row.CreatedAt.Year, Is.GreaterThanOrEqualTo(2025),
                "NOT named in keep, and ServerDefaults omits it unconditionally — this is the documented behaviour");
        });
    }
}
