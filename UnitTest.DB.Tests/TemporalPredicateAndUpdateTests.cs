using System.Linq;
using UnitTest.DB;

namespace UnitTest.DB.Tests;

/// <summary>
/// Live tests for the read/filter-side normalization that the write paths already had: a DateTimeOffset value in
/// a WHERE predicate, and a selective (WithFields) update of a DateTimeOffset column. Plus WithFields edge cases.
/// </summary>
[TestFixture]
public class TemporalPredicateAndUpdateTests : BaseUnitTest
{
    [SetUp]
    public Task Clean() => ClearAsync("test_counters");

    // A non-UTC DateTimeOffset bound into a WHERE used to throw (the read path didn't normalize the offset like
    // the write path); it now binds the UTC instant and matches.
    [Test]
    public async Task Where_NonUtcDateTimeOffset_DoesNotThrow_AndMatches()
    {
        var when = new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.FromHours(3));
        var id = Guid.NewGuid();
        await new TestCounter { Id = id, Label = "tz", CreatedTz = when }
            .Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await TestCounter.Query(x => x.CreatedTz <= when)
            .WithConnection(Connection).ExecuteAsync().ToListAsync();

        Assert.That(rows.Select(r => r.Id), Does.Contain(id));
    }

    // An array `Contains` over a DateTimeOffset[] with non-UTC offsets (= ANY(@arr)): the array form binds to
    // MemoryExtensions.Contains (now unwrapped from its span conversion), and each element is normalized to UTC.
    [Test]
    public async Task Where_DateTimeOffsetArray_Contains_DoesNotThrow_AndMatches()
    {
        var when = new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.FromHours(3));
        var id = Guid.NewGuid();
        await new TestCounter { Id = id, Label = "any", CreatedTz = when }
            .Insert().WithConnection(Connection).ExecuteAsync();

        var set = new[] { when, when.AddHours(1) };
        var rows = await TestCounter.Query(x => set.Contains(x.CreatedTz))
            .WithConnection(Connection).ExecuteAsync().ToListAsync();

        Assert.That(rows.Select(r => r.Id), Does.Contain(id));
    }

    // A selective (WithFields) update of a DateTimeOffset column used to throw because the WithFields visitor's
    // NormalizeDbValue didn't normalize the offset; it now binds the UTC instant.
    [Test]
    public async Task WithFields_DateTimeOffset_DoesNotThrow()
    {
        var id = Guid.NewGuid();
        await new TestCounter { Id = id, Label = "a", CreatedTz = DateTimeOffset.UnixEpoch }
            .Insert().WithConnection(Connection).ExecuteAsync();

        var newTz = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5));
        int rows = await new TestCounter { Id = id, CreatedTz = newTz }
            .Update().WithConnection(Connection).WithFields(x => new object[] { x.CreatedTz }).ExecuteAsync();

        Assert.That(rows, Is.EqualTo(1));
    }

    // An empty WithFields selector now throws a clear error instead of emitting malformed "SET  WHERE ...".
    [Test]
    public void WithFields_Empty_ThrowsClearError()
    {
        var item = new TestCounter { Id = Guid.NewGuid(), Label = "x" };
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await item.Update().WithConnection(Connection).WithFields(x => new object[] { }).ExecuteAsync());
    }

    // A duplicate member in a WithFields selector is de-duplicated (PostgreSQL rejects "col = .., col = ..").
    [Test]
    public async Task WithFields_DuplicateMember_DoesNotEmitDuplicateAssignment()
    {
        var id = Guid.NewGuid();
        await new TestCounter { Id = id, Label = "orig" }
            .Insert().WithConnection(Connection).ExecuteAsync();

        int rows = await new TestCounter { Id = id, Label = "updated" }
            .Update().WithConnection(Connection).WithFields(x => new object[] { x.Label, x.Label }).ExecuteAsync();

        Assert.That(rows, Is.EqualTo(1));
    }
}
