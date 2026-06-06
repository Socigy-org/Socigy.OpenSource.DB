using Socigy.OpenSource.DB.Core.Migrations;

namespace UnitTest.DB.Tests;

/// <summary>
/// A migration's schema change and its version-table row must be applied atomically: either both persist or
/// neither does. Otherwise a crash between them leaves the schema changed-but-unrecorded (re-applied next
/// run) or recorded-but-not-changed.
/// </summary>
[TestFixture]
public class MigrationExecutorTests : BaseUnitTest
{
    private static string Name() => "me_" + Guid.NewGuid().ToString("N").Substring(0, 12);

    private async Task<bool> TableExists(string name) =>
        (await ScalarAsync($"SELECT to_regclass('\"{name}\"')::text")) is not (null or DBNull);

    private async Task Drop(string name)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"DROP TABLE IF EXISTS \"{name}\"";
        await cmd.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task Schema_change_and_version_row_commit_together()
    {
        string t = Name();
        bool recorded = false;
        try
        {
            await MigrationExecutor.ApplyAtomicAsync(Connection,
                $"CREATE TABLE \"{t}\" (id int NOT NULL);",
                async tx =>
                {
                    await using System.Data.Common.DbCommand c = Connection.CreateCommand();
                    c.Transaction = tx;
                    c.CommandText = $"INSERT INTO \"{t}\" (id) VALUES (1);";
                    await c.ExecuteNonQueryAsync();
                    recorded = true;
                });

            Assert.That(recorded, Is.True);
            Assert.That(await TableExists(t), Is.True, "table should be committed");
            Assert.That(await CountAsync(t), Is.EqualTo(1), "version-equivalent row should be committed");
        }
        finally { await Drop(t); }
    }

    [Test]
    public async Task Failing_schema_sql_rolls_everything_back()
    {
        string t = Name();
        // CREATE succeeds, then a failing statement in the same step must roll back the CREATE too.
        Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
            await MigrationExecutor.ApplyAtomicAsync(Connection,
                $"CREATE TABLE \"{t}\" (id int); SELECT 1/0;",
                _ => Task.CompletedTask));

        Assert.That(await TableExists(t), Is.False, "a failed migration must leave no schema behind");
    }

    [Test]
    public async Task Failing_version_record_rolls_back_the_schema_change()
    {
        string t = Name();
        // The DDL succeeds but recording the version throws — the schema change must be rolled back so the
        // migration is not silently applied without being recorded.
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await MigrationExecutor.ApplyAtomicAsync(Connection,
                $"CREATE TABLE \"{t}\" (id int);",
                _ => throw new InvalidOperationException("recording failed")));

        Assert.That(await TableExists(t), Is.False, "schema must roll back when version recording fails");
    }
}
