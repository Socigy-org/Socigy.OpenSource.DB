using System;
using System.Linq;
using System.Threading.Tasks;
using UnitTest.DB;

namespace UnitTest.DB.Tests;

/// <summary>
/// The AOT-safe string overloads (insert keep, query Select/OrderBy, update WithFields/ExceptFields, join OrderBy)
/// must behave identically to their Expression counterparts. These avoid the <c>Expression.NewArrayInit</c> that
/// breaks NativeAOT, naming columns by string instead.
/// </summary>
[TestFixture]
public class AotStringOverloadTests : BaseUnitTest
{
    [SetUp]
    public async Task Clean()
    {
        await ClearAsync("test_items");
        await ClearAsync("test_counters");
    }

    // ── insert keep (string) ─────────────────────────────────────────────────
    [Test]
    public async Task ExcludeAutoFields_ByName_KeepsTheNamedColumn()
    {
        var id = Guid.NewGuid();
        bool ok = await new TestItem { Id = id, Name = "keep-name", Priority = 5 }
            .Insert().WithConnection(Connection)
            .ExcludeAutoFields(nameof(TestItem.Id))   // string overload: supply Id ourselves, server fills CreatedAt
            .ExecuteAsync();
        Assert.That(ok, Is.True);

        var rows = await TestItem.Query(x => x.Id == id).WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Id, Is.EqualTo(id));
        Assert.That(rows[0].CreatedAt, Is.Not.EqualTo(default(DateTime)), "CreatedAt should be filled by the DB default");
    }

    [Test]
    public async Task InsertMultiple_KeepColumns_ByName()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        int n = await TestItem.InsertMultipleAsync(
            new[] { new TestItem { Id = id1, Name = "a", Priority = 1 }, new TestItem { Id = id2, Name = "b", Priority = 2 } },
            Connection, new[] { nameof(TestItem.Id) });
        Assert.That(n, Is.EqualTo(2));

        var rows = await TestItem.Query(x => x.Name == "a" || x.Name == "b").WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(rows.Select(r => r.Id), Is.EquivalentTo(new[] { id1, id2 }), "the supplied Ids must be kept");
    }

    // ── query Select / OrderBy (string) ──────────────────────────────────────
    [Test]
    public async Task Select_ByName_ProjectsOnlyNamedColumns()
    {
        var id = Guid.NewGuid();
        await new TestItem { Id = id, Name = "proj", Priority = 9 }.Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await TestItem.Query(x => x.Id == id).Select(nameof(TestItem.Name))
            .WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Name, Is.EqualTo("proj"));
        Assert.That(rows[0].Id, Is.EqualTo(Guid.Empty), "Id was not projected, so it materializes as default");
    }

    [Test]
    public async Task OrderBy_And_OrderByDesc_ByName()
    {
        for (int p = 1; p <= 3; p++)
            await new TestItem { Id = Guid.NewGuid(), Name = $"ob-{p}", Priority = p }.Insert().WithConnection(Connection).ExecuteAsync();

        var asc = await TestItem.Query(x => x.Name.StartsWith("ob-")).OrderBy(nameof(TestItem.Priority))
            .WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(asc.Select(r => r.Priority), Is.EqualTo(new[] { 1, 2, 3 }));

        var desc = await TestItem.Query(x => x.Name.StartsWith("ob-")).OrderByDesc(nameof(TestItem.Priority))
            .WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(desc.Select(r => r.Priority), Is.EqualTo(new[] { 3, 2, 1 }));
    }

    // ── update WithFields / ExceptFields (string) ────────────────────────────
    [Test]
    public async Task Update_WithFields_ByName_UpdatesOnlyThatColumn()
    {
        var id = Guid.NewGuid();
        await new TestItem { Id = id, Name = "before", Priority = 1 }.Insert().WithConnection(Connection).ExecuteAsync();

        var changed = new TestItem { Id = id, Name = "after", Priority = 999 };
        await changed.Update().WithConnection(Connection).WithFields(nameof(TestItem.Name)).ExecuteAsync();

        var rows = await TestItem.Query(x => x.Id == id).WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(rows[0].Name, Is.EqualTo("after"), "Name updated by WithFields(string)");
        Assert.That(rows[0].Priority, Is.EqualTo(1), "Priority not selected, so unchanged");
    }

    [Test]
    public async Task Update_ExceptFields_ByName_UpdatesEverythingElse()
    {
        var id = Guid.NewGuid();
        await new TestItem { Id = id, Name = "before", Priority = 1 }.Insert().WithConnection(Connection).ExecuteAsync();

        var changed = new TestItem { Id = id, Name = "after2", Priority = 42 };
        await changed.Update().WithConnection(Connection).ExceptFields(nameof(TestItem.Name)).ExecuteAsync();

        var rows = await TestItem.Query(x => x.Id == id).WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(rows[0].Name, Is.EqualTo("before"), "Name excluded, so unchanged");
        Assert.That(rows[0].Priority, Is.EqualTo(42), "Priority updated (not excluded)");
    }

    // ── join OrderBy (string, driving table) ─────────────────────────────────
    [Test]
    public async Task Join_OrderBy_ByName_OrdersByDrivingTable()
    {
        var label = $"jo-{Guid.NewGuid():N}";
        for (int p = 3; p >= 1; p--)
        {
            await new TestItem { Id = Guid.NewGuid(), Name = label, Priority = p }.Insert().WithConnection(Connection).ExecuteAsync();
        }
        await new TestCounter { Id = Guid.NewGuid(), Label = label }.Insert().WithConnection(Connection).ExecuteAsync();

        var results = await TestItem.Query(x => x.Name == label)
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .OrderBy(nameof(TestItem.Priority))
            .WithConnection(Connection)
            .ToListAsync();

        Assert.That(results.Select(r => r.Left!.Priority), Is.EqualTo(new[] { 1, 2, 3 }),
            "join order-by by driving-table column name must sort ascending");
    }
}
