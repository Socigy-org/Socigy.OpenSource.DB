using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace UnitTest.DB.Tests;

/// <summary>
/// Tests for set operations (UNION, UNION ALL, INTERSECT, EXCEPT) using
/// <see cref="Socigy.OpenSource.DB.Core.PostgresqlSetQueryCommandBuilder{T}"/>.
/// </summary>
[TestFixture]
public class SetOperationTests
{
    [OneTimeSetUp]
    public Task Init() => UnitCore.InitializeAsync();

    // ── UNION ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Union_TwoDisjointSets_DeduplicatesRows()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        var prefix = $"union-{Guid.NewGuid():N}";
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        await TestItem.InsertAsync(new TestItem { Id = id1, Name = $"{prefix}-a", Priority = 1 }, conn);
        await TestItem.InsertAsync(new TestItem { Id = id2, Name = $"{prefix}-b", Priority = 2 }, conn);

        var lhs = TestItem.Query().Where(x => x.Id == id1);
        var rhs = TestItem.Query().Where(x => x.Id == id2);

        var results = new List<TestItem>();
        await foreach (var row in lhs.Union(rhs).WithConnection(conn).ExecuteAsync())
            results.Add(row);

        var ids = new HashSet<Guid>(results.ConvertAll(r => r.Id));
        Assert.That(ids, Does.Contain(id1));
        Assert.That(ids, Does.Contain(id2));
    }

    [Test]
    public async Task Union_SameRow_DeduplicatesSingleRow()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        var id = Guid.NewGuid();
        await TestItem.InsertAsync(new TestItem { Id = id, Name = $"union-dedup-{id:N}", Priority = 7 }, conn);

        var lhs = TestItem.Query().Where(x => x.Id == id);
        var rhs = TestItem.Query().Where(x => x.Id == id);

        var results = new List<TestItem>();
        await foreach (var row in lhs.Union(rhs).WithConnection(conn).ExecuteAsync())
            results.Add(row);

        Assert.That(results.FindAll(r => r.Id == id), Has.Count.EqualTo(1));
    }

    // ── UNION ALL ───────────────────────────────────────────────────────────

    [Test]
    public async Task UnionAll_SameRow_ReturnsDuplicates()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        var id = Guid.NewGuid();
        await TestItem.InsertAsync(new TestItem { Id = id, Name = $"union-all-{id:N}", Priority = 9 }, conn);

        var lhs = TestItem.Query().Where(x => x.Id == id);
        var rhs = TestItem.Query().Where(x => x.Id == id);

        var results = new List<TestItem>();
        await foreach (var row in lhs.UnionAll(rhs).WithConnection(conn).ExecuteAsync())
            results.Add(row);

        Assert.That(results.FindAll(r => r.Id == id), Has.Count.EqualTo(2));
    }

    // ── INTERSECT ───────────────────────────────────────────────────────────

    [Test]
    public async Task Intersect_TwoOverlappingSets_ReturnsCommonRows()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        var prefix = $"intersect-{Guid.NewGuid():N}";
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid();

        // A and B share priority 50; C has priority 99
        await TestItem.InsertAsync(new TestItem { Id = idA, Name = $"{prefix}-a", Priority = 50 }, conn);
        await TestItem.InsertAsync(new TestItem { Id = idB, Name = $"{prefix}-b", Priority = 50 }, conn);
        await TestItem.InsertAsync(new TestItem { Id = idC, Name = $"{prefix}-c", Priority = 99 }, conn);

        // LHS: items with ids in {A, B} ; RHS: items with ids in {B, C}
        // INTERSECT should return only B (only row common to both)
        var lhs = TestItem.Query().Where(x => x.Id == idA || x.Id == idB);
        var rhs = TestItem.Query().Where(x => x.Id == idB || x.Id == idC);

        var results = new List<TestItem>();
        await foreach (var row in lhs.Intersect(rhs).WithConnection(conn).ExecuteAsync())
            results.Add(row);

        var ids = new HashSet<Guid>(results.ConvertAll(r => r.Id));
        Assert.That(ids, Does.Contain(idB));
        Assert.That(ids, Does.Not.Contain(idA));
        Assert.That(ids, Does.Not.Contain(idC));
    }

    // ── EXCEPT ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Except_RemovesRhsRows_ReturnsLhsMinusRhs()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        var prefix = $"except-{Guid.NewGuid():N}";
        var idKeep = Guid.NewGuid();
        var idRemove = Guid.NewGuid();

        await TestItem.InsertAsync(new TestItem { Id = idKeep, Name = $"{prefix}-keep", Priority = 11 }, conn);
        await TestItem.InsertAsync(new TestItem { Id = idRemove, Name = $"{prefix}-remove", Priority = 11 }, conn);

        var lhs = TestItem.Query().Where(x => x.Id == idKeep || x.Id == idRemove);
        var rhs = TestItem.Query().Where(x => x.Id == idRemove);

        var results = new List<TestItem>();
        await foreach (var row in lhs.Except(rhs).WithConnection(conn).ExecuteAsync())
            results.Add(row);

        var ids = new HashSet<Guid>(results.ConvertAll(r => r.Id));
        Assert.That(ids, Does.Contain(idKeep));
        Assert.That(ids, Does.Not.Contain(idRemove));
    }
}
