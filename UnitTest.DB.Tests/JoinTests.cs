using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace UnitTest.DB.Tests;

/// <summary>
/// Tests for JOIN queries via the generated <c>Query().Join&lt;…&gt;()</c> builders: 2/3/4-table joins,
/// OrderBy, client-side projection, aggregates, the driving-table predicate, outer-join NULLs, and the
/// shared parameter counter across ON + driving-predicate + WHERE.
/// </summary>
[TestFixture]
public class JoinTests : BaseUnitTest
{
    private sealed record NameLabel(string Name, string Label);

    // ── INNER JOIN ──────────────────────────────────────────────────────────
    [Test]
    public async Task InnerJoin_MatchingRows_ReturnsTuples()
    {
        var label = $"ij-{Guid.NewGuid():N}";
        var itemId = Guid.NewGuid();
        var counterId = Guid.NewGuid();
        await TestItem.InsertAsync(new TestItem { Id = itemId, Name = label, Priority = 1 }, Connection);
        await TestCounter.InsertAsync(new TestCounter { Id = counterId, Label = label }, Connection);

        var results = await TestItem.Query()
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .Where((item, counter) => item.Id == itemId)
            .WithConnection(Connection)
            .ToListAsync();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Left!.Id, Is.EqualTo(itemId));
        Assert.That(results[0].Right!.Id, Is.EqualTo(counterId));
    }

    // A joined-table column whose name is long enough that "a1_<name>" exceeds Postgres's 63-byte identifier limit
    // read back NULL with the old name-embedding alias (the result label truncated but the lookup string did not).
    // With the positional alias it round-trips.
    [Test]
    public async Task InnerJoin_LongColumnName_RoundTrips()
    {
        var label = $"ljc-{Guid.NewGuid():N}";
        var itemId = Guid.NewGuid();
        const string longValue = "round-tripped";
        await TestItem.InsertAsync(new TestItem { Id = itemId, Name = label, Priority = 1 }, Connection);
        await TestCounter.InsertAsync(new TestCounter
        {
            Id = Guid.NewGuid(),
            Label = label,
            LongJoinColumnNameUsedToExceedSixtyThreeAliasBoundary = longValue,
        }, Connection);

        var results = await TestItem.Query()
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .Where((item, counter) => item.Id == itemId)
            .WithConnection(Connection)
            .ToListAsync();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Right!.LongJoinColumnNameUsedToExceedSixtyThreeAliasBoundary, Is.EqualTo(longValue),
            "a joined column with a >63-byte output alias must round-trip, not read as NULL");
    }

    [Test]
    public async Task InnerJoin_NoMatch_ReturnsEmpty()
    {
        var itemId = Guid.NewGuid();
        await TestItem.InsertAsync(new TestItem { Id = itemId, Name = $"nm-{Guid.NewGuid():N}", Priority = 5 }, Connection);

        var results = await TestItem.Query()
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .Where((item, counter) => item.Id == itemId)
            .WithConnection(Connection)
            .ToListAsync();

        Assert.That(results, Is.Empty);
    }

    // ── LEFT JOIN — outer side is NULL on miss ───────────────────────────────
    [Test]
    public async Task LeftJoin_NoMatchingCounter_ReturnsNullRight()
    {
        var itemId = Guid.NewGuid();
        await TestItem.InsertAsync(new TestItem { Id = itemId, Name = $"lj-{Guid.NewGuid():N}", Priority = 3 }, Connection);

        var results = await TestItem.Query()
            .LeftJoin<TestCounter>((item, counter) => item.Name == counter.Label)
            .Where((item, counter) => item.Id == itemId)
            .WithConnection(Connection)
            .ToListAsync();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Left!.Id, Is.EqualTo(itemId));
        Assert.That(results[0].Right, Is.Null, "unmatched right side must be null, not a default instance");
    }

    [Test]
    public async Task LeftJoinMiss_Through_Select_SeesNull()
    {
        var itemId = Guid.NewGuid();
        var name = $"ljs-{Guid.NewGuid():N}";
        await TestItem.InsertAsync(new TestItem { Id = itemId, Name = name, Priority = 3 }, Connection);

        var labels = await TestItem.Query()
            .LeftJoin<TestCounter>((item, counter) => item.Name == counter.Label)
            .Where((item, counter) => item.Id == itemId)
            .WithConnection(Connection)
            .Select((item, counter) => counter == null ? "(none)" : counter.Label)
            .ToListAsync();

        Assert.That(labels, Has.Count.EqualTo(1));
        Assert.That(labels[0], Is.EqualTo("(none)"));
    }

    // ── CROSS JOIN ──────────────────────────────────────────────────────────
    [Test]
    public async Task CrossJoin_ReturnsCartesianProduct()
    {
        var prefix = $"cross-{Guid.NewGuid():N}";
        var itemId = Guid.NewGuid();
        await TestItem.InsertAsync(new TestItem { Id = itemId, Name = $"{prefix}-a", Priority = 10 }, Connection);
        var counterIds = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var cid in counterIds)
            await TestCounter.InsertAsync(new TestCounter { Id = cid, Label = $"{prefix}-{cid:N}" }, Connection);

        var results = await TestItem.Query()
            .CrossJoin<TestCounter>()
            .Where((item, counter) => item.Id == itemId)
            .WithConnection(Connection)
            .ToListAsync();

        Assert.That(results.Count(r => counterIds.Contains(r.Right!.Id)), Is.EqualTo(2));
    }

    // ── Driving predicate (the bug fix) ──────────────────────────────────────
    [Test]
    public async Task DrivingPredicate_FiltersMainTable()
    {
        var label = $"drv-{Guid.NewGuid():N}";
        var keepId = Guid.NewGuid();
        var dropId = Guid.NewGuid();
        await TestItem.InsertAsync(new TestItem { Id = keepId, Name = label, Priority = 1 }, Connection);
        await TestItem.InsertAsync(new TestItem { Id = dropId, Name = label, Priority = 1 }, Connection);
        await TestCounter.InsertAsync(new TestCounter { Id = Guid.NewGuid(), Label = label }, Connection);

        var results = await TestItem.Query(item => item.Id == keepId)   // must filter the driving table
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .WithConnection(Connection)
            .ToListAsync();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Left!.Id, Is.EqualTo(keepId));
    }

    // ── 3-table join ─────────────────────────────────────────────────────────
    [Test]
    public async Task ThreeTableJoin_ReturnsTriple()
    {
        var label = $"j3-{Guid.NewGuid():N}";
        var itemId = Guid.NewGuid();
        await TestItem.InsertAsync(new TestItem { Id = itemId, Name = label, Priority = 7 }, Connection);
        await TestCounter.InsertAsync(new TestCounter { Id = Guid.NewGuid(), Label = label }, Connection);
        var typeId = Guid.NewGuid();
        await TestType.InsertAsync(new TestType { Id = typeId, Amount = 7m, IsActive = true }, Connection);

        var results = await TestItem.Query(item => item.Id == itemId)
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .Join<TestType>((item, counter, type) => type.Amount == item.Priority)
            .Where((item, counter, type) => type.Id == typeId)   // shared test tables aren't cleaned; pin the joined row
            .WithConnection(Connection)
            .ToListAsync();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Item1!.Id, Is.EqualTo(itemId));
        Assert.That(results[0].Item3!.Id, Is.EqualTo(typeId));
    }

    // ── 4-table join (max arity) ─────────────────────────────────────────────
    [Test]
    public async Task FourTableJoin_ReturnsQuad()
    {
        var label = $"j4-{Guid.NewGuid():N}";
        var itemId = Guid.NewGuid();
        await TestItem.InsertAsync(new TestItem { Id = itemId, Name = label, Priority = 9 }, Connection);
        await TestCounter.InsertAsync(new TestCounter { Id = Guid.NewGuid(), Label = label }, Connection);
        var typeId = Guid.NewGuid();
        await TestType.InsertAsync(new TestType { Id = typeId, Amount = 9m, IsActive = true }, Connection);
        var convId = Guid.NewGuid();
        await TestConvertorItem.InsertAsync(new TestConvertorItem { Id = convId, Label = "x", Value = 9 }, Connection);

        var results = await TestItem.Query(item => item.Id == itemId)
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .Join<TestType>((item, counter, type) => type.Amount == item.Priority)
            .Join<TestConvertorItem>((item, counter, type, conv) => conv.Value == item.Priority)
            .Where((item, counter, type, conv) => type.Id == typeId && conv.Id == convId)   // pin joined rows (shared tables)
            .WithConnection(Connection)
            .ToListAsync();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Item1!.Id, Is.EqualTo(itemId));
        Assert.That(results[0].Item4!.Id, Is.EqualTo(convId));
    }

    // ── OrderBy / OrderByDesc ────────────────────────────────────────────────
    [Test]
    public async Task OrderByDesc_OrdersRows()
    {
        var label = $"ord-{Guid.NewGuid():N}";
        await TestItem.InsertAsync(new TestItem { Id = Guid.NewGuid(), Name = label, Priority = 1 }, Connection);
        await TestItem.InsertAsync(new TestItem { Id = Guid.NewGuid(), Name = label, Priority = 2 }, Connection);
        await TestCounter.InsertAsync(new TestCounter { Id = Guid.NewGuid(), Label = label }, Connection);

        var results = await TestItem.Query(item => item.Name == label)
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .OrderByDesc((item, counter) => new object?[] { item.Priority })
            .WithConnection(Connection)
            .ToListAsync();

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results[0].Left!.Priority, Is.EqualTo(2));
        Assert.That(results[1].Left!.Priority, Is.EqualTo(1));
    }

    // ── Projection to a typed DTO ────────────────────────────────────────────
    [Test]
    public async Task Select_ProjectsToDto()
    {
        var label = $"proj-{Guid.NewGuid():N}";
        var itemId = Guid.NewGuid();
        await TestItem.InsertAsync(new TestItem { Id = itemId, Name = label, Priority = 1 }, Connection);
        await TestCounter.InsertAsync(new TestCounter { Id = Guid.NewGuid(), Label = label }, Connection);

        var dtos = await TestItem.Query(item => item.Id == itemId)
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .WithConnection(Connection)
            .Select((item, counter) => new NameLabel(item!.Name, counter!.Label))
            .ToListAsync();

        Assert.That(dtos, Has.Count.EqualTo(1));
        Assert.That(dtos[0], Is.EqualTo(new NameLabel(label, label)));
    }

    // ── Aggregates over a join ───────────────────────────────────────────────
    [Test]
    public async Task Aggregates_Count_And_Sum_OverJoin()
    {
        var label = $"agg-{Guid.NewGuid():N}";
        await TestItem.InsertAsync(new TestItem { Id = Guid.NewGuid(), Name = label, Priority = 10 }, Connection);
        await TestItem.InsertAsync(new TestItem { Id = Guid.NewGuid(), Name = label, Priority = 20 }, Connection);
        await TestCounter.InsertAsync(new TestCounter { Id = Guid.NewGuid(), Label = label }, Connection);

        long count = await TestItem.Query(item => item.Name == label)
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .WithConnection(Connection)
            .CountAsync();
        Assert.That(count, Is.EqualTo(2));

        int? sum = await TestItem.Query(item => item.Name == label)
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .WithConnection(Connection)
            .SumAsync<int>((item, counter) => item.Priority);
        Assert.That(sum, Is.EqualTo(30));
    }

    // A join aggregate over a DateTimeOffset column used raw Convert.ChangeType (which can't target
    // DateTimeOffset — Npgsql returns timestamptz as a UTC DateTime), throwing InvalidCastException. It now
    // routes through ApplyDbValue like the single-table aggregate.
    [Test]
    public async Task JoinAggregate_MaxDateTimeOffset_RoundTrips()
    {
        var label = $"jtz-{Guid.NewGuid():N}";
        var when = new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.FromHours(2));
        await TestItem.InsertAsync(new TestItem { Id = Guid.NewGuid(), Name = label, Priority = 1 }, Connection);
        await TestCounter.InsertAsync(new TestCounter { Id = Guid.NewGuid(), Label = label, CreatedTz = when }, Connection);

        var max = await TestItem.Query(item => item.Name == label)
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .WithConnection(Connection)
            .MaxAsync<DateTimeOffset>((item, counter) => counter.CreatedTz);

        Assert.That(max, Is.Not.Null);
        Assert.That(max!.Value.ToUniversalTime(), Is.EqualTo(when.ToUniversalTime()));
    }

    [Test]
    public async Task EmptyAggregate_ReturnsNullSum_AndZeroCount()
    {
        var label = $"empty-{Guid.NewGuid():N}";
        await TestItem.InsertAsync(new TestItem { Id = Guid.NewGuid(), Name = label, Priority = 5 }, Connection);
        // No counter with this label → the join matches nothing.

        long count = await TestItem.Query(item => item.Name == label)
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .WithConnection(Connection)
            .CountAsync();
        Assert.That(count, Is.EqualTo(0));

        int? sum = await TestItem.Query(item => item.Name == label)
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .WithConnection(Connection)
            .SumAsync<int>((item, counter) => item.Priority);
        Assert.That(sum, Is.Null, "SUM over an empty set is SQL NULL → null");
    }

    // ── Shared parameter counter across ON + driving predicate + WHERE ────────
    [Test]
    public async Task ConstantsInAllThreeClauses_NoParamCollision()
    {
        var label = $"pc-{Guid.NewGuid():N}";
        var itemId = Guid.NewGuid();
        var counterId = Guid.NewGuid();
        await TestItem.InsertAsync(new TestItem { Id = itemId, Name = label, Priority = 42 }, Connection);
        await TestCounter.InsertAsync(new TestCounter { Id = counterId, Label = label }, Connection);
        // A decoy item with the same name but a different priority — only excluded if every clause's
        // constant binds to a distinct parameter.
        await TestItem.InsertAsync(new TestItem { Id = Guid.NewGuid(), Name = label, Priority = 99 }, Connection);

        var results = await TestItem.Query(item => item.Priority == 42)                       // driving: @p?=42
            .Join<TestCounter>((item, counter) => item.Name == counter.Label && counter.Id != Guid.Empty) // ON: @p?=empty guid
            .Where((item, counter) => item.Name == label)                                     // WHERE: @p?=label
            .WithConnection(Connection)
            .ToListAsync();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Left!.Id, Is.EqualTo(itemId));
        Assert.That(results[0].Right!.Id, Is.EqualTo(counterId));
    }
}
