using UnitTest.DB;

namespace UnitTest.DB.Tests;

[TestFixture]
public class UpdateTests : BaseUnitTest
{
    private Guid _seedId;

    [SetUp]
    public async Task Seed()
    {
        await ClearAsync("test_items");
        _seedId = Guid.NewGuid();
        var item = new TestItem { Id = _seedId, Name = "Original", Priority = 1 };
        await item.Insert().WithConnection(Connection).ExecuteAsync();
    }

    // ------------------------------------------------------------------
    // UpdateAsync static helper
    // ------------------------------------------------------------------

    [Test]
    public async Task UpdateAsync_StaticHelper_UpdatesAllFields()
    {
        var updated = new TestItem { Id = _seedId, Name = "Updated", Priority = 99 };

        int rows = await TestItem.UpdateAsync(updated, Connection);

        Assert.That(rows, Is.EqualTo(1));

        var fetched = await TestItem.Query(x => x.Id == _seedId)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(fetched[0].Name, Is.EqualTo("Updated"));
        Assert.That(fetched[0].Priority, Is.EqualTo(99));
    }

    // ------------------------------------------------------------------
    // Instance builder
    // ------------------------------------------------------------------

    [Test]
    public async Task Update_WithAllFields_UpdatesRow()
    {
        var item = new TestItem { Id = _seedId, Name = "FromBuilder", Priority = 50 };

        int rows = await item.Update()
            .WithConnection(Connection)
            .WithAllFields()
            .ExecuteAsync();

        Assert.That(rows, Is.EqualTo(1));

        var fetched = await TestItem.Query(x => x.Id == _seedId)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(fetched[0].Name, Is.EqualTo("FromBuilder"));
    }

    [Test]
    public async Task Update_WithFields_OnlySelectedColumnsChange()
    {
        var item = new TestItem { Id = _seedId, Name = "NameOnly", Priority = 999 };

        await item.Update()
            .WithConnection(Connection)
            .WithFields(x => new object[] { x.Name })
            .ExecuteAsync();

        var fetched = await TestItem.Query(x => x.Id == _seedId)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(fetched[0].Name, Is.EqualTo("NameOnly"));
        Assert.That(fetched[0].Priority, Is.EqualTo(1), "Priority not in WithFields — unchanged");
    }

    [Test]
    public async Task Update_ExceptFields_SkipsSpecifiedColumn()
    {
        var item = new TestItem { Id = _seedId, Name = "Changed", Priority = 77 };

        await item.Update()
            .WithConnection(Connection)
            .ExceptFields(x => new object[] { x.Priority })
            .ExecuteAsync();

        var fetched = await TestItem.Query(x => x.Id == _seedId)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(fetched[0].Name, Is.EqualTo("Changed"));
        Assert.That(fetched[0].Priority, Is.EqualTo(1), "Priority excluded — should remain 1");
    }

    // ------------------------------------------------------------------
    // WHERE filter on update
    // ------------------------------------------------------------------

    [Test]
    public async Task Update_WhereFilter_OnlyMatchingRowsUpdated()
    {
        // Add a second row
        var other = new TestItem { Id = Guid.NewGuid(), Name = "Other", Priority = 2 };
        await other.Insert().WithConnection(Connection).ExecuteAsync();

        var item = new TestItem { Id = _seedId, Name = "Filtered", Priority = 10 };

        await item.Update()
            .WithConnection(Connection)
            .WithAllFields()
            .Where(x => x.Id == _seedId)
            .ExecuteAsync();

        var all = await TestItem.Query()
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        var seed = all.First(x => x.Id == _seedId);
        var otherFetched = all.First(x => x.Id == other.Id);

        Assert.That(seed.Name, Is.EqualTo("Filtered"));
        Assert.That(otherFetched.Name, Is.EqualTo("Other"), "Other row should be untouched");
    }

    // ------------------------------------------------------------------
    // Update non-existent row
    // ------------------------------------------------------------------

    [Test]
    public async Task Update_NonExistentRow_ReturnsZeroAffected()
    {
        var ghost = new TestItem { Id = Guid.NewGuid(), Name = "Ghost", Priority = 0 };

        int rows = await ghost.Update()
            .WithConnection(Connection)
            .WithAllFields()
            .ExecuteAsync();

        Assert.That(rows, Is.EqualTo(0));
    }
}
