using System.Data.Common;
using Npgsql;
using Socigy.OpenSource.DB.Core;
using Socigy.OpenSource.DB.Core.Context;
using Socigy.OpenSource.DB.TestDb.Context;
using UnitTest.DB;
using static Socigy.OpenSource.DB.Core.SyntaxHelper.DB;

namespace UnitTest.DB.Tests;

/// <summary>
/// Live-PostgreSQL tests for the <c>[TableType]</c> / <c>DynamicTable&lt;T&gt;</c> feature: runtime-named typed
/// tables, full CRUD + aggregates standalone and through a context, opt-in custom (undeclared) columns,
/// <c>DB.CustomField</c> filtering, auto-mapping with caching, and table lifecycle (create/exists/drop).
/// </summary>
[TestFixture]
public class DynamicTableTests : BaseUnitTest
{
    private sealed class TestConnectionFactory : IDbConnectionFactory
    {
        public DbConnection Create(string? connectionKey = null) => UnitCore.CreateConnection();
        public Task<bool> EnsureDbExists() => Task.FromResult(true);
    }

    private static TestDbFactory NewFactory(ConnectionLifetime lifetime = ConnectionLifetime.PerScope)
        => new(new TestConnectionFactory(), new SocigyDbContextOptions { ConnectionLifetime = lifetime });

    private async Task DropAsync(string table)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = $@"DROP TABLE IF EXISTS ""{table}""";
        await cmd.ExecuteNonQueryAsync();
    }

    private AuditEntry NewEntry(Guid user, string action, int amount) => new()
    {
        Id = Guid.NewGuid(),
        UserId = user,
        Action = action,
        Amount = amount,
        At = new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Unspecified)
    };

    [Test]
    public async Task Lifecycle_Crud_And_Aggregates_ViaConnection()
    {
        const string table = "rt_audit_conn";
        await DropAsync(table);

        Assert.That(await AuditEntry.WithTableName(table).WithConnection(Connection).InstanceExistsAsync(), Is.False);
        await AuditEntry.WithTableName(table).WithConnection(Connection).InstantiateAsync();
        Assert.That(await AuditEntry.WithTableName(table).WithConnection(Connection).InstanceExistsAsync(), Is.True);

        var user = Guid.NewGuid();
        await AuditEntry.WithTableName(table).WithConnection(Connection).InsertAsync(NewEntry(user, "login", 10));
        await AuditEntry.WithTableName(table).WithConnection(Connection).InsertMultipleAsync(new[]
        {
            NewEntry(user, "click", 20),
            NewEntry(user, "click", 30),
            NewEntry(Guid.NewGuid(), "login", 5),
        });

        Assert.That(await AuditEntry.WithTableName(table).WithConnection(Connection).CountAsync(), Is.EqualTo(4));
        Assert.That(await AuditEntry.WithTableName(table).WithConnection(Connection).Query(x => x.Action == "click").CountAsync(), Is.EqualTo(2));
        Assert.That(await AuditEntry.WithTableName(table).WithConnection(Connection).Query(x => x.UserId == user).SumAsync<int>(x => x.Amount), Is.EqualTo(60));
        Assert.That(await AuditEntry.WithTableName(table).WithConnection(Connection).MaxAsync<int>(x => x.Amount), Is.EqualTo(30));

        var clicks = await AuditEntry.WithTableName(table).WithConnection(Connection)
            .Query(x => x.Action == "click").OrderBy("\"amount\" DESC").ToListAsync();
        Assert.That(clicks, Has.Count.EqualTo(2));
        Assert.That(clicks[0].Amount, Is.EqualTo(30));

        var first = await AuditEntry.WithTableName(table).WithConnection(Connection).Query(x => x.Amount == 10).FirstOrDefaultAsync();
        Assert.That(first, Is.Not.Null);
        Assert.That(first!.Action, Is.EqualTo("login"));

        // Update the matching rows' Action, then verify.
        int updated = await AuditEntry.WithTableName(table).WithConnection(Connection)
            .UpdateAsync(NewEntry(user, "click2", 99), x => x.Action == "click");
        Assert.That(updated, Is.EqualTo(2));
        Assert.That(await AuditEntry.WithTableName(table).WithConnection(Connection).Query(x => x.Action == "click2").CountAsync(), Is.EqualTo(2));

        int deleted = await AuditEntry.WithTableName(table).WithConnection(Connection).DeleteAsync(x => x.Action == "login");
        Assert.That(deleted, Is.EqualTo(2));
        Assert.That(await AuditEntry.WithTableName(table).WithConnection(Connection).CountAsync(), Is.EqualTo(2));

        await AuditEntry.WithTableName(table).WithConnection(Connection).DeleteInstanceAsync();
        Assert.That(await AuditEntry.WithTableName(table).WithConnection(Connection).InstanceExistsAsync(), Is.False);
    }

    [Test]
    public async Task TwoNames_SameType_HitDifferentTables()
    {
        const string a = "rt_audit_a", b = "rt_audit_b";
        await DropAsync(a); await DropAsync(b);
        await AuditEntry.WithTableName(a).WithConnection(Connection).InstantiateAsync();
        await AuditEntry.WithTableName(b).WithConnection(Connection).InstantiateAsync();
        try
        {
            await AuditEntry.WithTableName(a).WithConnection(Connection).InsertAsync(NewEntry(Guid.NewGuid(), "x", 1));
            Assert.That(await AuditEntry.WithTableName(a).WithConnection(Connection).CountAsync(), Is.EqualTo(1));
            Assert.That(await AuditEntry.WithTableName(b).WithConnection(Connection).CountAsync(), Is.EqualTo(0));
        }
        finally
        {
            await DropAsync(a); await DropAsync(b);
        }
    }

    [Test]
    public async Task Context_DynamicTable_RoundTrips()
    {
        const string table = "rt_audit_ctx";
        await DropAsync(table);
        await AuditEntry.WithTableName(table).WithConnection(Connection).InstantiateAsync();
        try
        {
            var factory = NewFactory();
            var user = Guid.NewGuid();

            await factory.ExecuteTransactionAsync(async db =>
            {
                var dyn = db.DynamicTable<AuditEntry>(table);

                await dyn.InsertAsync(NewEntry(user, "a", 7));
                await dyn.InsertAsync(NewEntry(user, "b", 8));
            });

            long count = await factory.ExecuteAsync(db => db.DynamicTable<AuditEntry>(table).Query(x => x.UserId == user).CountAsync());
            Assert.That(count, Is.EqualTo(2));

            var rows = await factory.ExecuteAsync(db => db.DynamicTable<AuditEntry>(table).Query(x => x.Amount >= 8).ToListAsync());
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].Action, Is.EqualTo("b"));
        }
        finally { await DropAsync(table); }
    }

    [Test]
    public async Task CustomColumns_And_CustomField_And_MapType()
    {
        const string table = "rt_audit_custom";
        await DropAsync(table);

        // A runtime table with two columns beyond the declared AuditEntry shape.
        await using (var create = Connection.CreateCommand())
        {
            create.CommandText = $@"CREATE TABLE ""{table}"" (
                ""id"" uuid PRIMARY KEY, ""user_id"" uuid, ""action"" text, ""amount"" integer, ""at"" timestamp,
                ""region"" text, ""extra_num"" integer)";
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var id1 = Guid.NewGuid();
            await using (var ins = Connection.CreateCommand())
            {
                ins.CommandText = $@"INSERT INTO ""{table}"" (id, user_id, action, amount, at, region, extra_num)
                    VALUES (@id, @u, 'e', 1, NOW(), 'eu', 42), (@id2, @u, 'f', 2, NOW(), 'us', 5)";
                ins.Parameters.Add(new NpgsqlParameter("id", id1));
                ins.Parameters.Add(new NpgsqlParameter("id2", Guid.NewGuid()));
                ins.Parameters.Add(new NpgsqlParameter("u", Guid.NewGuid()));
                await ins.ExecuteNonQueryAsync();
            }

            Assert.That(await CountAsync(table), Is.EqualTo(2), "raw row count");
            Assert.That(await AuditEntry.WithTableName(table).WithConnection(Connection).CountAsync(), Is.EqualTo(2), "dynamic count");
            Assert.That(Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM \"{table}\" WHERE action = 'e'")), Is.EqualTo(1), "raw filtered count");
            Assert.That(await AuditEntry.WithTableName(table).WithConnection(Connection).Query(x => x.Action == "e").CountAsync(), Is.EqualTo(1), "dynamic filtered count");

            // Explicit custom columns + read-back.
            var rows = await AuditEntry.WithTableName(table).WithConnection(Connection)
                .WithCustomColumns("region", "extra_num")
                .Query(x => x.Action == "e").ToListAsync();
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(((global::Socigy.OpenSource.DB.Core.Interfaces.IDbTableType<AuditEntry>)rows[0]).TryGetCustomValue<string>("region", out var region), Is.True);
            Assert.That(region, Is.EqualTo("eu"));
            Assert.That(rows[0].TryGetCustomValue<int>("extra_num", out var num) && num == 42, Is.True);

            // Filter on an undeclared column via DB.CustomField.
            var hi = await AuditEntry.WithTableName(table).WithConnection(Connection)
                .Query(x => CustomField<int>("extra_num") > 10).ToListAsync();
            Assert.That(hi, Has.Count.EqualTo(1));
            Assert.That(hi[0].Action, Is.EqualTo("e"));

            // Auto-map discovers the extras without naming them.
            var mapped = await AuditEntry.MapTypeAsync(table, Connection);
            var all = await mapped.Query(x => x.Amount >= 1).ToListAsync();
            Assert.That(all, Has.Count.EqualTo(2));
            Assert.That(all.Any(r => r.TryGetCustomValue<string>("region", out var rg) && rg == "us"), Is.True);
        }
        finally { await DropAsync(table); }
    }
}
