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

    // InstantiateAsync baked an enum column as "text", but the insert binds the enum as its underlying integer,
    // so the insert failed (integer -> text). The baked DDL now types the enum column as the integral type.
    [Test]
    public async Task Instantiate_EnumColumn_TypedAsInteger_RoundTrips()
    {
        const string table = "rt_audit_enum";
        await DropAsync(table);
        await AuditEntry.WithTableName(table).WithConnection(Connection).InstantiateAsync();

        var entry = NewEntry(Guid.NewGuid(), "x", 1);
        entry.Status = WorkStatus.Active;
        await AuditEntry.WithTableName(table).WithConnection(Connection).InsertAsync(entry);

        var back = await AuditEntry.WithTableName(table).WithConnection(Connection)
            .Query(x => x.Id == entry.Id).FirstOrDefaultAsync();
        Assert.That(back, Is.Not.Null);
        Assert.That(back!.Status, Is.EqualTo(WorkStatus.Active));

        // Confirm the column is an integer type, not text.
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"SELECT data_type FROM information_schema.columns WHERE table_name = '{table}' AND column_name = 'status'";
        Assert.That(await cmd.ExecuteScalarAsync() as string, Is.EqualTo("integer"));

        await DropAsync(table);
    }

    // The baked InstantiateAsync DDL created a non-nullable string/byte[] column as NULLABLE (it forced every
    // reference type nullable), while the migration generator creates it NOT NULL. The two ways of creating the
    // same table must agree: a non-nullable `string Action` column must be NOT NULL.
    [Test]
    public async Task Instantiate_NonNullableStringColumn_IsNotNull()
    {
        const string table = "rt_audit_notnull";
        await DropAsync(table);
        await AuditEntry.WithTableName(table).WithConnection(Connection).InstantiateAsync();

        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"SELECT is_nullable FROM information_schema.columns WHERE table_name = '{table}' AND column_name = 'action'";
        Assert.That(await cmd.ExecuteScalarAsync() as string, Is.EqualTo("NO"),
            "a non-nullable string column must be created NOT NULL, matching the migration DDL");

        await DropAsync(table);
    }

    // A custom [AutoIncrement("name")] sequence used serial in the baked DDL, which creates {table}_{col}_seq
    // instead of the custom name, so the runtime sequence accessor (which uses the custom name) threw
    // "relation does not exist". InstantiateAsync now creates the custom-named sequence.
    [Test]
    public async Task Instantiate_CustomNamedAutoIncrementSequence_AccessorWorks()
    {
        const string table = "rt_audit_seq";
        await DropAsync(table);
        await using (var drop = Connection.CreateCommand())
        {
            drop.CommandText = "DROP SEQUENCE IF EXISTS \"audit_entry_custom_seq\"";
            await drop.ExecuteNonQueryAsync();
        }

        await AuditEntry.WithTableName(table).WithConnection(Connection).InstantiateAsync();

        // The custom-named sequence must exist now; the accessor would otherwise throw.
        long next = await AuditEntry.CounterSequence.GetNextValueAsync(Connection);
        Assert.That(next, Is.GreaterThan(0));

        await DropAsync(table);
    }

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
                ""region"" text, ""extra_num"" integer, ""custom_tz"" timestamptz)";
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var id1 = Guid.NewGuid();
            await using (var ins = Connection.CreateCommand())
            {
                ins.CommandText = $@"INSERT INTO ""{table}"" (id, user_id, action, amount, at, region, extra_num, custom_tz)
                    VALUES (@id, @u, 'e', 1, NOW(), 'eu', 42, NOW()), (@id2, @u, 'f', 2, NOW(), 'us', 5, NOW())";
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
                .WithCustomColumns("region", "extra_num", "custom_tz")
                .Query(x => x.Action == "e").ToListAsync();
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(((global::Socigy.OpenSource.DB.Core.Interfaces.IDbTableType<AuditEntry>)rows[0]).TryGetCustomValue<string>("region", out var region), Is.True);
            Assert.That(region, Is.EqualTo("eu"));
            Assert.That(rows[0].TryGetCustomValue<int>("extra_num", out var num) && num == 42, Is.True);
            // A custom timestamptz column read as DateTimeOffset must succeed (Convert.ChangeType throws for
            // DateTimeOffset; the width-tolerant converter maps the timestamptz DateTime onto it).
            Assert.That(rows[0].TryGetCustomValue<DateTimeOffset>("custom_tz", out var tz), Is.True,
                "a custom timestamptz column must read back as DateTimeOffset, like a declared column");
            Assert.That(tz, Is.Not.EqualTo(default(DateTimeOffset)));

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

    // Regression: a [Default(...)] column on a [TableType] got NOT NULL but no DEFAULT clause in the baked
    // CREATE TABLE, so InstantiateAsync produced a schema that diverged from migrations and an insert relying on
    // the server default failed. The baked DDL must emit the DEFAULT (here, gen_random_uuid() for the Id PK).
    [Test]
    public async Task Instantiate_DefaultColumn_BakesServerDefault()
    {
        const string table = "rt_audit_default";
        await DropAsync(table);
        await AuditEntry.WithTableName(table).WithConnection(Connection).InstantiateAsync();
        try
        {
            await using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT column_default FROM information_schema.columns " +
                              "WHERE table_name = @t AND column_name = 'id'";
            cmd.Parameters.Add(new NpgsqlParameter("t", table));
            var def = await cmd.ExecuteScalarAsync() as string;
            Assert.That(def, Does.Contain("gen_random_uuid"),
                "the [Default(Guid.Random)] PK must bake a server DEFAULT into the instantiated table");
        }
        finally { await DropAsync(table); }
    }

    // Regression: DynamicTable's aggregate/scalar coercion used Convert.ChangeType directly, which crashes for an
    // enum result (the column is stored as its underlying int, and int→enum is not an IConvertible cast) and for a
    // DateTimeOffset result. It must use the shared ApplyDbValue converter the join aggregate path uses.
    [Test]
    public async Task DynamicTable_EnumAggregateAndScalar_DoNotCrash()
    {
        const string table = "rt_audit_enum_agg";
        await DropAsync(table);
        await AuditEntry.WithTableName(table).WithConnection(Connection).InstantiateAsync();
        try
        {
            var pending = NewEntry(Guid.NewGuid(), "a", 1); pending.Status = WorkStatus.Pending;
            var active = NewEntry(Guid.NewGuid(), "b", 2); active.Status = WorkStatus.Active;
            var done = NewEntry(Guid.NewGuid(), "c", 3); done.Status = WorkStatus.Done;
            await AuditEntry.WithTableName(table).WithConnection(Connection).InsertMultipleAsync(new[] { pending, active, done });

            // MAX over the enum column: Done has the highest underlying int. Before the fix this threw
            // InvalidCastException (Convert.ChangeType(int, typeof(WorkStatus))).
            var max = await AuditEntry.WithTableName(table).WithConnection(Connection).MaxAsync<WorkStatus>(x => x.Status);
            Assert.That(max, Is.EqualTo(WorkStatus.Done));

            var min = await AuditEntry.WithTableName(table).WithConnection(Connection).MinAsync<WorkStatus>(x => x.Status);
            Assert.That(min, Is.EqualTo(WorkStatus.Pending));

            // ScalarAsync reading the enum column back also went through the crashing coercion.
            var scalar = await AuditEntry.WithTableName(table).WithConnection(Connection)
                .Query(x => x.Action == "b").ScalarAsync<WorkStatus>(x => x.Status);
            Assert.That(scalar, Is.EqualTo(WorkStatus.Active));
        }
        finally { await DropAsync(table); }
    }
}
