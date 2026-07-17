using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnitTest.DB;

namespace UnitTest.DB.Tests;

/// <summary>
/// Live regressions for two field reports against 0.3.5:
/// (1) an inequality combined with an equality key returning zero rows, and
/// (2) a member access on a captured loop variable "binding to default" and filtering to no rows.
/// Both are exercised end to end here, including the repeated-query path that engages the query-shape cache
/// (the first call translates and caches; later calls replay the plan and re-bind).
/// </summary>
[TestFixture]
public class PredicateRegressionTests : BaseUnitTest
{
    [SetUp]
    public async Task Clean() => await ClearAsync("test_items");

    // ── != combined with an equality key ────────────────────────────────────────────
    [Test]
    public async Task NotEqual_WithEqualityKey_ReturnsTheComplement()
    {
        string label = $"ne-{Guid.NewGuid():N}";
        for (int p = 1; p <= 3; p++)
            await new TestItem { Id = Guid.NewGuid(), Name = label, Priority = p }
                .Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await TestItem.Query(x => x.Name == label && x.Priority != 2)
            .WithConnection(Connection).ExecuteAsync().ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2), "the inequality must return the complement, not an empty set");
        Assert.That(rows.Select(r => r.Priority), Is.EquivalentTo(new[] { 1, 3 }));
    }

    [Test]
    public async Task NotEqual_OnGuidKey_ExcludesOnlyThatRow()
    {
        string label = $"neg-{Guid.NewGuid():N}";
        var ids = new List<Guid>();
        for (int p = 1; p <= 3; p++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await new TestItem { Id = id, Name = label, Priority = p }
                .Insert().WithConnection(Connection).ExecuteAsync();
        }

        var rows = await TestItem.Query(x => x.Name == label && x.Id != ids[1])
            .WithConnection(Connection).ExecuteAsync().ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2), "a Guid inequality must exclude exactly one row");
        Assert.That(rows.Select(r => r.Id), Is.EquivalentTo(new[] { ids[0], ids[2] }));
    }

    // Same shape run repeatedly: the second and later calls hit the cached plan, so an == and a != on the
    // same column must never reuse each other's SQL (that would return exactly the wrong rows).
    [Test]
    public async Task EqualAndNotEqual_OnTheSameColumn_DoNotShareCachedSql()
    {
        string label = $"cache-{Guid.NewGuid():N}";
        for (int p = 1; p <= 3; p++)
            await new TestItem { Id = Guid.NewGuid(), Name = label, Priority = p }
                .Insert().WithConnection(Connection).ExecuteAsync();

        for (int round = 0; round < 3; round++)
        {
            var eq = await TestItem.Query(x => x.Name == label && x.Priority == 2)
                .WithConnection(Connection).ExecuteAsync().ToListAsync();
            var ne = await TestItem.Query(x => x.Name == label && x.Priority != 2)
                .WithConnection(Connection).ExecuteAsync().ToListAsync();

            Assert.That(eq.Select(r => r.Priority), Is.EqualTo(new[] { 2 }), $"round {round}: == shape");
            Assert.That(ne.Select(r => r.Priority), Is.EquivalentTo(new[] { 1, 3 }), $"round {round}: != shape");
        }
    }

    // ── member access on a captured loop variable ───────────────────────────────────
    // `m.Id == link.Id` folds link.Id to a parameter. Iteration 1 caches the SQL and a path to that
    // sub-expression; later iterations replay the plan and must re-evaluate it against the CURRENT loop
    // variable. Binding a stale (or default) value would silently return the wrong rows or none.
    [Test]
    public async Task CapturedLoopVariableMember_BindsTheCurrentIterationsValue()
    {
        string label = $"loop-{Guid.NewGuid():N}";
        for (int p = 1; p <= 4; p++)
            await new TestItem { Id = Guid.NewGuid(), Name = label, Priority = p }
                .Insert().WithConnection(Connection).ExecuteAsync();

        var links = await TestItem.Query(x => x.Name == label)
            .WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(links, Has.Count.EqualTo(4));

        foreach (var link in links)
        {
            // Deliberately NOT hoisted into a local: the whole point is the member access on the closure.
            var byId = await TestItem.Query(m => m.Id == link.Id)
                .WithConnection(Connection).ExecuteAsync().ToListAsync();

            Assert.That(byId, Has.Count.EqualTo(1),
                $"iteration for priority {link.Priority} must match its own row, not default/none");
            Assert.That(byId[0].Id, Is.EqualTo(link.Id));
            Assert.That(byId[0].Priority, Is.EqualTo(link.Priority));

            // A second column, and combined with an equality key, to cover the composite shape too.
            var byPriority = await TestItem.Query(m => m.Name == label && m.Priority == link.Priority)
                .WithConnection(Connection).ExecuteAsync().ToListAsync();
            Assert.That(byPriority.Select(r => r.Priority), Is.EqualTo(new[] { link.Priority }));
        }
    }

    [Test]
    public async Task CapturedLoopVariableMember_WithInequality()
    {
        string label = $"loopne-{Guid.NewGuid():N}";
        for (int p = 1; p <= 3; p++)
            await new TestItem { Id = Guid.NewGuid(), Name = label, Priority = p }
                .Insert().WithConnection(Connection).ExecuteAsync();

        var links = await TestItem.Query(x => x.Name == label)
            .WithConnection(Connection).ExecuteAsync().ToListAsync();

        foreach (var link in links)
        {
            // The reported shape: equality key + inequality against a captured row member.
            var others = await TestItem.Query(m => m.Name == label && m.Id != link.Id)
                .WithConnection(Connection).ExecuteAsync().ToListAsync();

            Assert.That(others, Has.Count.EqualTo(2), "every row except the captured one");
            Assert.That(others.Select(r => r.Id), Does.Not.Contain(link.Id));
        }
    }
}
