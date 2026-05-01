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
}
