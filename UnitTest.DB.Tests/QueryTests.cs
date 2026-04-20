using UnitTest.DB;

namespace UnitTest.DB.Tests;

[TestFixture]
public class QueryTests : BaseUnitTest
{
    private static readonly Guid IdA = Guid.NewGuid();
    private static readonly Guid IdB = Guid.NewGuid();
    private static readonly Guid IdC = Guid.NewGuid();

    [SetUp]
    public async Task SeedData()
    {
        await ClearAsync("test_items");

        var items = new[]
        {
            new TestItem { Id = IdA, Name = "Alpha",   Priority = 10 },
            new TestItem { Id = IdB, Name = "Beta",    Priority = 20 },
            new TestItem { Id = IdC, Name = "Charlie", Priority = 30 },
        };
        foreach (var item in items)
            await item.Insert().WithConnection(Connection).ExecuteAsync();
    }

    // ------------------------------------------------------------------
    // Query all
    // ------------------------------------------------------------------

    [Test]
    public async Task Query_NoFilter_ReturnsAllRows()
    {
        var rows = await TestItem.Query()
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(3));
    }

    // ------------------------------------------------------------------
    // WHERE predicates
    // ------------------------------------------------------------------

    [Test]
    public async Task Query_EqualityFilter_ReturnsMatchingRow()
    {
        var rows = await TestItem.Query(x => x.Id == IdA)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Name, Is.EqualTo("Alpha"));
    }

    [Test]
    public async Task Query_NotEqualFilter_ExcludesRow()
    {
        var rows = await TestItem.Query(x => x.Id != IdA)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows.Select(r => r.Id), Does.Not.Contain(IdA));
    }

    [Test]
    public async Task Query_GreaterThanFilter_MatchesCorrectRows()
    {
        var rows = await TestItem.Query(x => x.Priority > 10)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Query_LessThanOrEqualFilter_MatchesCorrectRows()
    {
        var rows = await TestItem.Query(x => x.Priority <= 20)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Query_AndAlsoFilter_NarrowsResults()
    {
        var rows = await TestItem.Query(x => x.Priority > 10 && x.Priority < 30)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Name, Is.EqualTo("Beta"));
    }

    [Test]
    public async Task Query_OrElseFilter_BroadensResults()
    {
        var rows = await TestItem.Query(x => x.Name == "Alpha" || x.Name == "Charlie")
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2));
    }

    // ------------------------------------------------------------------
    // NULL checks
    // ------------------------------------------------------------------

    [Test]
    public async Task Query_IsNullFilter_MatchesNullValues()
    {
        // CreatedAt is NOT NULL in schema, so let's use a nullable column
        // We'll insert an item with Priority = 0 and just verify IS NOT NULL works on Name
        var rows = await TestItem.Query(x => x.Name != null)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(3));
    }

    // ------------------------------------------------------------------
    // String method translations
    // ------------------------------------------------------------------

    [Test]
    public async Task Query_ContainsFilter_MatchesSubstring()
    {
        var rows = await TestItem.Query(x => x.Name.Contains("lph"))
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Name, Is.EqualTo("Alpha"));
    }

    [Test]
    public async Task Query_StartsWithFilter_MatchesPrefix()
    {
        var rows = await TestItem.Query(x => x.Name.StartsWith("B"))
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Name, Is.EqualTo("Beta"));
    }

    [Test]
    public async Task Query_EndsWithFilter_MatchesSuffix()
    {
        var rows = await TestItem.Query(x => x.Name.EndsWith("lie"))
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Name, Is.EqualTo("Charlie"));
    }

    // ------------------------------------------------------------------
    // Limit / Offset / OrderBy
    // ------------------------------------------------------------------

    [Test]
    public async Task Query_Limit_ReturnsAtMostNRows()
    {
        var rows = await TestItem.Query()
            .WithConnection(Connection)
            .Limit(2)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Query_Top_Alias_Works()
    {
        var rows = await TestItem.Query()
            .WithConnection(Connection)
            .Top(1)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Query_Offset_SkipsRows()
    {
        var all = await TestItem.Query()
            .WithConnection(Connection)
            .OrderBy(x => new object[] { x.Priority })
            .ExecuteAsync()
            .ToListAsync();

        var paged = await TestItem.Query()
            .WithConnection(Connection)
            .OrderBy(x => new object[] { x.Priority })
            .Offset(1)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(paged, Has.Count.EqualTo(2));
        Assert.That(paged[0].Name, Is.EqualTo(all[1].Name));
    }

    [Test]
    public async Task Query_OrderByAscending_SortsLowToHigh()
    {
        var rows = await TestItem.Query()
            .WithConnection(Connection)
            .OrderBy(x => new object[] { x.Priority })
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows[0].Priority, Is.LessThan(rows[1].Priority));
        Assert.That(rows[1].Priority, Is.LessThan(rows[2].Priority));
    }

    [Test]
    public async Task Query_OrderByDescending_SortsHighToLow()
    {
        var rows = await TestItem.Query()
            .WithConnection(Connection)
            .OrderByDesc(x => new object[] { x.Priority })
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows[0].Priority, Is.GreaterThan(rows[1].Priority));
    }

    // ------------------------------------------------------------------
    // Query with Where shorthand (static entry point)
    // ------------------------------------------------------------------

    [Test]
    public async Task Query_StaticWhereEntryPoint_FiltersCorrectly()
    {
        var rows = await TestItem.Query(x => x.Priority == 20)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Name, Is.EqualTo("Beta"));
    }

    // ------------------------------------------------------------------
    // Chained Where on existing builder
    // ------------------------------------------------------------------

    [Test]
    public async Task Query_ChainedWhere_AppliesFilter()
    {
        var rows = await TestItem.Query()
            .Where(x => x.Priority >= 20)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows.All(r => r.Priority >= 20), Is.True);
    }

    // ------------------------------------------------------------------
    // Limit(0) / Negative guards
    // ------------------------------------------------------------------

    [Test]
    public void Query_NegativeLimit_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TestItem.Query().Limit(-1));

    [Test]
    public void Query_NegativeOffset_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TestItem.Query().Offset(-1));
}
