using UnitTest.DB;

namespace UnitTest.DB.Tests;

[TestFixture]
public class DeleteTests : BaseUnitTest
{
    private Guid _idA;
    private Guid _idB;

    [SetUp]
    public async Task Seed()
    {
        await ClearAsync("test_items");
        _idA = Guid.NewGuid();
        _idB = Guid.NewGuid();

        await new TestItem { Id = _idA, Name = "Row A", Priority = 1 }
            .Insert().WithConnection(Connection).ExecuteAsync();
        await new TestItem { Id = _idB, Name = "Row B", Priority = 2 }
            .Insert().WithConnection(Connection).ExecuteAsync();
    }

    // ------------------------------------------------------------------
    // Delete by instance (PK-based)
    // ------------------------------------------------------------------

    [Test]
    public async Task Delete_ByInstance_RemovesOnlyThatRow()
    {
        var item = new TestItem { Id = _idA };

        int rows = await item.Delete()
            .WithConnection(Connection)
            .ExecuteAsync();

        Assert.That(rows, Is.EqualTo(1));
        Assert.That(await CountAsync("test_items"), Is.EqualTo(1));
    }

    [Test]
    public async Task Delete_ByInstance_OtherRowUntouched()
    {
        var item = new TestItem { Id = _idA };
        await item.Delete().WithConnection(Connection).ExecuteAsync();

        var remaining = await TestItem.Query()
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(remaining[0].Id, Is.EqualTo(_idB));
    }

    // ------------------------------------------------------------------
    // DeleteNonInstance with WHERE
    // ------------------------------------------------------------------

    [Test]
    public async Task DeleteNonInstance_WhereFilter_RemovesMatchingRows()
    {
        int rows = await TestItem.DeleteNonInstance()
            .WithConnection(Connection)
            .Where(x => x.Priority > 1)
            .ExecuteAsync();

        Assert.That(rows, Is.EqualTo(1));
        Assert.That(await CountAsync("test_items"), Is.EqualTo(1));
    }

    [Test]
    public async Task DeleteNonInstance_WhereMatchesAll_RemovesAll()
    {
        int rows = await TestItem.DeleteNonInstance()
            .WithConnection(Connection)
            .Where(x => x.Priority >= 1)
            .ExecuteAsync();

        Assert.That(rows, Is.EqualTo(2));
        Assert.That(await CountAsync("test_items"), Is.EqualTo(0));
    }

    [Test]
    public async Task DeleteNonInstance_WhereMatchesNone_RemovesNone()
    {
        int rows = await TestItem.DeleteNonInstance()
            .WithConnection(Connection)
            .Where(x => x.Priority > 999)
            .ExecuteAsync();

        Assert.That(rows, Is.EqualTo(0));
        Assert.That(await CountAsync("test_items"), Is.EqualTo(2));
    }

    // ------------------------------------------------------------------
    // Delete non-existent row
    // ------------------------------------------------------------------

    [Test]
    public async Task Delete_NonExistentId_ReturnsZero()
    {
        var ghost = new TestItem { Id = Guid.NewGuid() };

        int rows = await ghost.Delete()
            .WithConnection(Connection)
            .ExecuteAsync();

        Assert.That(rows, Is.EqualTo(0));
    }
}
