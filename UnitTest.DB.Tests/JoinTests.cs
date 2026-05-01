using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace UnitTest.DB.Tests;

/// <summary>
/// Tests for JOIN queries using <see cref="Socigy.OpenSource.DB.Core.PostgresqlJoinQueryCommandBuilder{T,TJoin}"/>.
/// We join <see cref="TestItem"/> with <see cref="TestCounter"/> so the two tables are distinct.
/// </summary>
[TestFixture]
public class JoinTests
{
    [OneTimeSetUp]
    public Task Init() => UnitCore.InitializeAsync();

    // ── INNER JOIN ──────────────────────────────────────────────────────────

    [Test]
    public async Task InnerJoin_MatchingRows_ReturnsTuples()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        // Seed a counter with a known label equal to an item's name
        var sharedLabel = $"join-inner-{Guid.NewGuid():N}";
        var itemId = Guid.NewGuid();
        var counterId = Guid.NewGuid();

        await TestItem.InsertAsync(new TestItem { Id = itemId, Name = sharedLabel, Priority = 1 }, conn);
        await TestCounter.InsertAsync(new TestCounter { Id = counterId, Label = sharedLabel }, conn);

        var results = new List<(TestItem, TestCounter)>();
        await foreach (var pair in TestItem.Query()
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .Where((item, counter) => item.Id == itemId)
            .WithConnection(conn)
            .ExecuteAsync())
        {
            results.Add(pair);
        }

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Item1.Id, Is.EqualTo(itemId));
        Assert.That(results[0].Item2.Id, Is.EqualTo(counterId));
    }

    [Test]
    public async Task InnerJoin_NoMatch_ReturnsEmpty()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        var itemId = Guid.NewGuid();
        await TestItem.InsertAsync(new TestItem { Id = itemId, Name = $"no-match-{Guid.NewGuid():N}", Priority = 5 }, conn);

        var results = new List<(TestItem, TestCounter)>();
        await foreach (var pair in TestItem.Query()
            .Join<TestCounter>((item, counter) => item.Name == counter.Label)
            .Where((item, counter) => item.Id == itemId)
            .WithConnection(conn)
            .ExecuteAsync())
        {
            results.Add(pair);
        }

        Assert.That(results, Is.Empty);
    }

    // ── LEFT JOIN ───────────────────────────────────────────────────────────

    [Test]
    public async Task LeftJoin_NoMatchingCounter_ReturnsItemWithNullCounter()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        var itemId = Guid.NewGuid();
        var uniqueName = $"left-join-{Guid.NewGuid():N}";
        await TestItem.InsertAsync(new TestItem { Id = itemId, Name = uniqueName, Priority = 3 }, conn);

        var results = new List<(TestItem, TestCounter)>();
        await foreach (var pair in TestItem.Query()
            .LeftJoin<TestCounter>((item, counter) => item.Name == counter.Label)
            .Where((item, counter) => item.Id == itemId)
            .WithConnection(conn)
            .ExecuteAsync())
        {
            results.Add(pair);
        }

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Item1.Id, Is.EqualTo(itemId));
        // Counter should have default values since there's no match
        Assert.That(results[0].Item2.Id, Is.EqualTo(Guid.Empty));
    }

    // ── CROSS JOIN ──────────────────────────────────────────────────────────

    [Test]
    public async Task CrossJoin_TwoRows_ReturnsCartesianProduct()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        var prefix = $"cross-{Guid.NewGuid():N}";
        var itemId1 = Guid.NewGuid();
        var itemId2 = Guid.NewGuid();

        await TestItem.InsertAsync(new TestItem { Id = itemId1, Name = $"{prefix}-a", Priority = 10 }, conn);
        await TestItem.InsertAsync(new TestItem { Id = itemId2, Name = $"{prefix}-b", Priority = 10 }, conn);

        var counterIds = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var cid in counterIds)
            await TestCounter.InsertAsync(new TestCounter { Id = cid, Label = $"{prefix}-{cid:N}" }, conn);

        var results = new List<(TestItem, TestCounter)>();
        await foreach (var pair in TestItem.Query()
            .CrossJoin<TestCounter>()
            .Where((item, counter) => item.Priority == 10 && item.Name == $"{prefix}-a")
            .WithConnection(conn)
            .ExecuteAsync())
        {
            if (counterIds.Contains(pair.Item2.Id))
                results.Add(pair);
        }

        // Each item matches with both counters (cartesian)
        Assert.That(results, Has.Count.GreaterThanOrEqualTo(2));
    }
}
