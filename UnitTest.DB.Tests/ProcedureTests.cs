using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnitTest.DB.Socigy.Generated;

namespace UnitTest.DB.Tests;

[TestFixture]
public class ProcedureTests
{
    [OneTimeSetUp]
    public Task Init() => UnitCore.InitializeAsync();

    // ── Void procedure (INSERT) ────────────────────────────────────────────

    [Test]
    public async Task VoidProcedure_Insert_ReturnsTrue()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        var id = Guid.NewGuid();
        var result = await Procedures.InsertTestItem(conn, id, "proc-test-item", 99);

        Assert.That(result, Is.True);
    }

    // ── Return-type procedure (SELECT) ────────────────────────────────────

    [Test]
    public async Task ReturnProcedure_GetByName_ReturnsMatchingRows()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        // Seed a row with a unique name
        var id = Guid.NewGuid();
        var uniqueName = $"proc-getbyname-{id:N}";
        await Procedures.InsertTestItem(conn, id, uniqueName, 7);

        // Query via the return-type procedure
        var rows = new List<TestItem>();
        await foreach (var item in Procedures.Items.GetByName(conn, uniqueName))
            rows.Add(item);

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Id, Is.EqualTo(id));
        Assert.That(rows[0].Name, Is.EqualTo(uniqueName));
        Assert.That(rows[0].Priority, Is.EqualTo(7));
    }

    [Test]
    public async Task ReturnProcedure_GetByName_NoMatch_ReturnsEmpty()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        var rows = new List<TestItem>();
        await foreach (var item in Procedures.Items.GetByName(conn, $"no-such-name-{Guid.NewGuid():N}"))
            rows.Add(item);

        Assert.That(rows, Is.Empty);
    }

    // ── Scalar procedure (-- @returns scalar: T) ──────────────────────────

    [Test]
    public async Task ScalarProcedure_Count_ReturnsInt()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        // Seed three rows sharing a unique name; COUNT(*) returns bigint, coerced to int.
        var name = $"proc-count-{Guid.NewGuid():N}";
        for (int i = 0; i < 3; i++)
            await Procedures.InsertTestItem(conn, Guid.NewGuid(), name, i);

        int count = await Procedures.CountItemsByName(conn, name);

        Assert.That(count, Is.EqualTo(3));
    }

    [Test]
    public async Task ScalarProcedure_NullableMax_NoMatch_ReturnsNull()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        int? max = await Procedures.MaxPriorityByName(conn, $"no-such-name-{Guid.NewGuid():N}");

        Assert.That(max, Is.Null);
    }

    [Test]
    public async Task ScalarProcedure_NullableMax_Match_ReturnsValue()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        var name = $"proc-max-{Guid.NewGuid():N}";
        await Procedures.InsertTestItem(conn, Guid.NewGuid(), name, 5);
        await Procedures.InsertTestItem(conn, Guid.NewGuid(), name, 12);

        int? max = await Procedures.MaxPriorityByName(conn, name);

        Assert.That(max, Is.EqualTo(12));
    }

    // ── Affected-count procedure (-- @returns affected) ───────────────────

    [Test]
    public async Task AffectedProcedure_Delete_ReturnsRowsAffected()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        var name = $"proc-del-{Guid.NewGuid():N}";
        await Procedures.InsertTestItem(conn, Guid.NewGuid(), name, 1);
        await Procedures.InsertTestItem(conn, Guid.NewGuid(), name, 2);

        int deleted = await Procedures.DeleteByName(conn, name);
        Assert.That(deleted, Is.EqualTo(2));

        int deletedAgain = await Procedures.DeleteByName(conn, name);
        Assert.That(deletedAgain, Is.EqualTo(0));
    }

    // ── DTO procedures (-- @returns: <non-[Table]>) ───────────────────────

    [Test]
    public async Task DtoProcedure_PositionalRecord_MapsByName()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        var name = $"proc-summary-{Guid.NewGuid():N}";
        await Procedures.InsertTestItem(conn, Guid.NewGuid(), name, 42);

        var rows = new List<ItemSummary>();
        await foreach (var s in Procedures.Items.GetSummaries(conn, name))
            rows.Add(s);

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Name, Is.EqualTo(name));
        Assert.That(rows[0].Priority, Is.EqualTo(42));
    }

    [Test]
    public async Task DtoProcedure_PropertyBag_UnboundMemberDefaultsToNull()
    {
        await using var conn = UnitCore.CreateConnection();
        await conn.OpenAsync();

        var name = $"proc-report-{Guid.NewGuid():N}";
        await Procedures.InsertTestItem(conn, Guid.NewGuid(), name, 7);

        var rows = new List<ItemReport>();
        await foreach (var r in Procedures.Items.GetReports(conn, name))
            rows.Add(r);

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Name, Is.EqualTo(name));
        Assert.That(rows[0].Priority, Is.EqualTo(7));
        Assert.That(rows[0].Missing, Is.Null, "a member with no matching result column maps to default");
    }
}
