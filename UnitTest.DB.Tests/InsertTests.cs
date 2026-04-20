using UnitTest.DB;

namespace UnitTest.DB.Tests;

[TestFixture]
public class InsertTests : BaseUnitTest
{
    [SetUp]
    public async Task Clean() => await ClearAsync("test_items");

    // ------------------------------------------------------------------
    // Basic insert
    // ------------------------------------------------------------------

    [Test]
    public async Task Insert_BasicInsert_RowAppearsInDb()
    {
        var item = new TestItem { Id = Guid.NewGuid(), Name = "Alpha", Priority = 1 };

        bool ok = await item.Insert()
            .WithConnection(Connection)
            .ExecuteAsync();

        Assert.That(ok, Is.True);
        Assert.That(await CountAsync("test_items"), Is.EqualTo(1));
    }

    [Test]
    public async Task InsertAsync_StaticHelper_Inserts()
    {
        var item = new TestItem { Id = Guid.NewGuid(), Name = "Beta", Priority = 2 };

        bool ok = await TestItem.InsertAsync(item, Connection);

        Assert.That(ok, Is.True);
    }

    [Test]
    public async Task Insert_MultipleRows_CountCorrect()
    {
        for (int i = 0; i < 5; i++)
        {
            var item = new TestItem { Id = Guid.NewGuid(), Name = $"Item{i}", Priority = i };
            await item.Insert().WithConnection(Connection).ExecuteAsync();
        }

        Assert.That(await CountAsync("test_items"), Is.EqualTo(5));
    }

    // ------------------------------------------------------------------
    // WithValuePropagation — DB-side defaults written back to instance
    // ------------------------------------------------------------------

    [Test]
    public async Task Insert_WithValuePropagation_DbDefaultIdPopulated()
    {
        // Id has [Default(DbDefaults.Guid.Random)] — let the DB generate it
        var item = new TestItem { Name = "Propagate", Priority = 10 };
        Assert.That(item.Id, Is.EqualTo(Guid.Empty), "precondition: Id not set");

        await item.Insert()
            .WithConnection(Connection)
            .ExcludeAutoFields()
            .WithValuePropagation()
            .ExecuteAsync();

        Assert.That(item.Id, Is.Not.EqualTo(Guid.Empty), "Id should be populated by RETURNING *");
    }

    [Test]
    public async Task Insert_WithValuePropagation_DbDefaultCreatedAtPopulated()
    {
        var item = new TestItem { Name = "Timestamp", Priority = 0 };

        await item.Insert()
            .WithConnection(Connection)
            .ExcludeAutoFields()
            .WithValuePropagation()
            .ExecuteAsync();

        Assert.That(item.CreatedAt, Is.Not.EqualTo(default(DateTime)), "CreatedAt should be set by DB default");
    }

    [Test]
    public async Task Insert_WithValuePropagation_ExplicitIdPreserved()
    {
        var explicitId = Guid.NewGuid();
        var item = new TestItem { Id = explicitId, Name = "ExplicitId", Priority = 5 };

        await item.Insert()
            .WithConnection(Connection)
            .WithValuePropagation()
            .ExecuteAsync();

        Assert.That(item.Id, Is.EqualTo(explicitId));
    }

    // ------------------------------------------------------------------
    // ExcludeAutoFields — auto-increment + defaults skipped from INSERT
    // ------------------------------------------------------------------

    [Test]
    public async Task Insert_ExcludeAutoFields_RowInserted()
    {
        var item = new TestItem { Name = "ExcludeAuto", Priority = 3 };

        bool ok = await item.Insert()
            .WithConnection(Connection)
            .ExcludeAutoFields()
            .ExecuteAsync();

        // Without WithValuePropagation, Id stays Guid.Empty (DB used default but we didn't read it back)
        Assert.That(ok, Is.True);
    }

    // ------------------------------------------------------------------
    // WithAllFields — forces auto-increment columns into the INSERT
    // ------------------------------------------------------------------

    [Test]
    public async Task Insert_WithAllFields_ExplicitIdInserted()
    {
        var id = Guid.NewGuid();
        var item = new TestItem { Id = id, Name = "AllFields", Priority = 99, CreatedAt = DateTime.UtcNow };

        bool ok = await item.Insert()
            .WithConnection(Connection)
            .WithAllFields()
            .ExecuteAsync();

        Assert.That(ok, Is.True);

        var rows = await TestItem.Query(x => x.Id == id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Name, Is.EqualTo("AllFields"));
    }

    // ------------------------------------------------------------------
    // WithFields / ExcludeFields
    // ------------------------------------------------------------------

    [Test]
    public async Task Insert_WithFields_OnlySpecifiedColumnsInserted()
    {
        var id = Guid.NewGuid();
        var item = new TestItem { Id = id, Name = "WithFields", Priority = 42 };

        bool ok = await item.Insert()
            .WithConnection(Connection)
            .WithFields(x => new object[] { x.Id, x.Name })
            .ExecuteAsync();

        Assert.That(ok, Is.True);

        var rows = await TestItem.Query(x => x.Id == id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows[0].Priority, Is.EqualTo(0), "Priority was not in WithFields — should be DB default 0");
    }

    [Test]
    public async Task Insert_ExcludeFields_SpecifiedColumnUsesDefault()
    {
        var id = Guid.NewGuid();
        var item = new TestItem { Id = id, Name = "ExcludeField", Priority = 77 };

        bool ok = await item.Insert()
            .WithConnection(Connection)
            .ExcludeFields(x => new object[] { x.Priority })
            .ExecuteAsync();

        Assert.That(ok, Is.True);

        var rows = await TestItem.Query(x => x.Id == id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows[0].Priority, Is.EqualTo(0), "Priority was excluded — DB default 0 expected");
    }

    // ------------------------------------------------------------------
    // WithAllFields + WithFields conflict guard
    // ------------------------------------------------------------------

    [Test]
    public void Insert_WithAllFields_ThenWithFields_Throws()
    {
        var item = new TestItem();
        Assert.Throws<InvalidOperationException>(() =>
            item.Insert().WithAllFields().WithFields(x => new object[] { x.Name }));
    }

    [Test]
    public void Insert_ExcludeAutoFields_ThenWithAllFields_Throws()
    {
        var item = new TestItem();
        Assert.Throws<InvalidOperationException>(() =>
            item.Insert().ExcludeAutoFields().WithAllFields());
    }
}

// Helper to collect IAsyncEnumerable into a List
internal static class AsyncEnumExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }
}
