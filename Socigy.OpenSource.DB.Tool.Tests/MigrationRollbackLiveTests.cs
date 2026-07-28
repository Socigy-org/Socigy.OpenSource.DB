using System;
using System.Threading.Tasks;
using Npgsql;
using NUnit.Framework;
using Socigy.OpenSource.DB.Core.Migrations;
using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using static Socigy.OpenSource.DB.Tool.Tests.TestSchema;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Executes generated migration scripts against a real PostgreSQL the way the generated migration manager
/// does: <see cref="MigrationExecutor.ApplyAtomicAsync"/> runs the DOWN script and the bookkeeping row in ONE
/// transaction. A DOWN that is merely "valid looking" is not enough, it has to commit together with that row.
///
/// Guards two rollback bugs:
///  - the DOWN dropped a table's sequence BEFORE the table whose DEFAULT nextval(...) depends on it
///    ("cannot drop sequence ... because other objects depend on it"), and
///  - the first migration's DOWN dropped the bookkeeping table itself, so the rollback row could never
///    be written.
///
/// Gated on a reachable PostgreSQL (env <c>SOCIGY_TEST_PG</c>).
/// </summary>
[TestFixture]
public class MigrationRollbackLiveTests
{
    private static string ConnString()
        => Environment.GetEnvironmentVariable("SOCIGY_TEST_PG")
           ?? "Host=127.0.0.1;Port=5432;Username=postgres;Password=1234;Database=postgres";

    private static async Task<NpgsqlConnection?> TryOpenAsync()
    {
        try { var c = new NpgsqlConnection(ConnString()); await c.OpenAsync(); return c; }
        catch { return null; }
    }

    private static async Task Exec(NpgsqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>True when <paramref name="name"/> resolves on the session search_path (table or sequence).</summary>
    private static async Task<bool> Exists(NpgsqlConnection c, string name)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT to_regclass('\"{name}\"')::text";
        return await cmd.ExecuteScalarAsync() is not (null or DBNull);
    }

    private static async Task<long> Count(NpgsqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Isolates the unqualified DDL the generator emits into a scratch schema.</summary>
    private static async Task<NpgsqlConnection?> OpenInScratchSchemaAsync(string schema)
    {
        var conn = await TryOpenAsync();
        if (conn is null) return null;
        await Exec(conn, $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE; CREATE SCHEMA ""{schema}"";");
        await Exec(conn, $@"SET search_path TO ""{schema}"";");
        return conn;
    }

    /// <summary>The migration history table as the source generator declares it.</summary>
    private static DbTable HistoryTable() => Table(MigrationHistory.TableName,
        Col("id", "bigint", pk: true, autoIncrement: true, dotnetType: "System.Int64"),
        Col("human_id", "text"),
        Col("applied_at", "timestamp without time zone"),
        Col("is_rollback", "boolean"),
        Col("executed_by", "text"));

    /// <summary>Mirrors the generated manager: schema change + version row in one transaction.</summary>
    private static Task ApplyAsync(NpgsqlConnection conn, string sql, string migrationId, bool isRollback)
        => MigrationExecutor.ApplyAtomicAsync(conn, sql, async tx =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (NpgsqlTransaction)tx;
            cmd.CommandText =
                $@"INSERT INTO ""{MigrationHistory.TableName}"" (""human_id"", ""applied_at"", ""is_rollback"", ""executed_by"")
                   VALUES (@id, now(), @rb, 'test');";
            cmd.Parameters.AddWithValue("id", migrationId);
            cmd.Parameters.AddWithValue("rb", isRollback);
            await cmd.ExecuteNonQueryAsync();
        });

    private static string Sql(System.Collections.Generic.IEnumerable<string> statements)
        => string.Join("\n", statements);

    // ── The root migration: its DOWN must run AND still be recordable ──
    // Before the fix this failed twice over: the DOWN dropped "_scg_migrations_id_seq" while the table's
    // DEFAULT still referenced it, and even past that it dropped the table the rollback row goes into.
    [Test]
    public async Task Root_migration_rolls_back_and_leaves_the_history_table_intact()
    {
        const string schema = "socigy_rb_root";
        var conn = await OpenInScratchSchemaAsync(schema);
        if (conn is null) { Assert.Ignore("No reachable PostgreSQL (set SOCIGY_TEST_PG)."); return; }
        await using var _ = conn;
        try
        {
            var history = HistoryTable();
            var users = Table("users", Col("id", "uuid", pk: true), Col("email", "text"));
            UseSchema(history, users);

            var (up, down) = new PostgreSqlGenerator()
                .Generate(new SchemaDiff { AddedTables = { users, history } }, isFirstMigration: true);

            // UP is applied before the history table exists, so its version row is written afterwards by the
            // same transaction (exactly what the generated manager does on a fresh database).
            await ApplyAsync(conn, Sql(up), "Initial Migration", isRollback: false);
            Assert.That(await Exists(conn, "users"), Is.True, "UP must create the user table");

            await ApplyAsync(conn, Sql(down), "Initial Migration", isRollback: true);

            bool usersGone = !await Exists(conn, "users");
            bool historyKept = await Exists(conn, MigrationHistory.TableName);
            bool historySeqKept = await Exists(conn, $"{MigrationHistory.TableName}_id_seq");
            long rollbackRows = await Count(conn,
                $@"SELECT COUNT(*) FROM ""{MigrationHistory.TableName}"" WHERE ""is_rollback""");

            Assert.Multiple(() =>
            {
                Assert.That(usersGone, Is.True, "DOWN must drop the user table");
                Assert.That(historyKept, Is.True,
                    "DOWN must leave the history table standing, the rollback row is written to it");
                Assert.That(historySeqKept, Is.True,
                    "DOWN must leave the history sequence standing, the id DEFAULT depends on it");
                Assert.That(rollbackRows, Is.EqualTo(1), "the rollback must be recorded");
            });
        }
        finally { await Exec(conn, $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE;"); }
    }

    // ── Rolling forward again after a rollback ──
    // The DOWN keeps the history table on purpose, so the next UP meets objects that already exist. This is
    // the full down-then-up cycle an operator actually performs after reverting a bad deploy.
    [Test]
    public async Task Root_migration_can_be_re_applied_after_being_rolled_back()
    {
        const string schema = "socigy_rb_replay";
        var conn = await OpenInScratchSchemaAsync(schema);
        if (conn is null) { Assert.Ignore("No reachable PostgreSQL (set SOCIGY_TEST_PG)."); return; }
        await using var _ = conn;
        try
        {
            var history = HistoryTable();
            var users = Table("users", Col("id", "uuid", pk: true), Col("email", "text"));
            UseSchema(history, users);

            var (up, down) = new PostgreSqlGenerator()
                .Generate(new SchemaDiff { AddedTables = { users, history } }, isFirstMigration: true);

            await ApplyAsync(conn, Sql(up), "Initial Migration", isRollback: false);
            await ApplyAsync(conn, Sql(down), "Initial Migration", isRollback: true);

            // Re-apply. The history table and its sequence survived the rollback, so this UP has to tolerate
            // finding them already there.
            await ApplyAsync(conn, Sql(up), "Initial Migration", isRollback: false);

            bool usersBack = await Exists(conn, "users");
            long rows = await Count(conn, $@"SELECT COUNT(*) FROM ""{MigrationHistory.TableName}""");

            Assert.Multiple(() =>
            {
                Assert.That(usersBack, Is.True, "the re-applied migration must recreate the user schema");
                Assert.That(rows, Is.EqualTo(3),
                    "apply, rollback and re-apply are each recorded, on the history table that survived");
            });
        }
        finally { await Exec(conn, $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE;"); }
    }

    // ── The realistic case: roll back exactly one non-root migration ──
    // Migration 2 both adds an [AutoIncrement] table and alters an existing one. Rolling it back must undo
    // both and leave migration 1's schema untouched.
    [Test]
    public async Task Single_non_root_migration_rolls_back_to_the_previous_schema()
    {
        const string schema = "socigy_rb_one";
        var conn = await OpenInScratchSchemaAsync(schema);
        if (conn is null) { Assert.Ignore("No reachable PostgreSQL (set SOCIGY_TEST_PG)."); return; }
        await using var _ = conn;
        try
        {
            // --- migration 1 (root): history + users ---
            var history = HistoryTable();
            var users = Table("users", Col("id", "uuid", pk: true), Col("email", "text"));
            UseSchema(history, users);

            var (up1, _unused) = new PostgreSqlGenerator()
                .Generate(new SchemaDiff { AddedTables = { users, history } }, isFirstMigration: true);
            await ApplyAsync(conn, Sql(up1), "m1", isRollback: false);

            // --- migration 2: add a table with an auto-increment key + a column on "users" ---
            var tickets = Table("tickets",
                Col("id", "bigint", pk: true, autoIncrement: true, dotnetType: "System.Int64"),
                Col("label", "text"));
            var usersV2 = Table("users", Col("id", "uuid", pk: true), Col("email", "text"),
                Col("nickname", "text", nullable: true));
            UseSchema(history, usersV2, tickets);

            var alteration = new TableAlteration { Table = usersV2 };
            alteration.ProvideDefaults();
            alteration.AddedColumns.Add(Col("nickname", "text", nullable: true));

            var (up2, down2) = new PostgreSqlGenerator().Generate(
                new SchemaDiff { AddedTables = { tickets }, AlteredTables = { alteration } }, isFirstMigration: false);

            await ApplyAsync(conn, Sql(up2), "m2", isRollback: false);
            bool ticketsUp = await Exists(conn, "tickets");
            bool ticketsSeqUp = await Exists(conn, "tickets_id_seq");
            bool nicknameUp = await ColumnExists(conn, schema, "users", "nickname");
            Assert.Multiple(() =>
            {
                Assert.That(ticketsUp, Is.True);
                Assert.That(ticketsSeqUp, Is.True);
                Assert.That(nicknameUp, Is.True);
            });

            // --- roll back ONLY migration 2 ---
            await ApplyAsync(conn, Sql(down2), "m2", isRollback: true);

            bool ticketsGone = !await Exists(conn, "tickets");
            bool ticketsSeqGone = !await Exists(conn, "tickets_id_seq");
            bool nicknameGone = !await ColumnExists(conn, schema, "users", "nickname");
            bool usersKept = await Exists(conn, "users");
            bool emailKept = await ColumnExists(conn, schema, "users", "email");
            bool historyKept = await Exists(conn, MigrationHistory.TableName);
            long historyRows = await Count(conn, $@"SELECT COUNT(*) FROM ""{MigrationHistory.TableName}""");

            Assert.Multiple(() =>
            {
                Assert.That(ticketsGone, Is.True, "DOWN must drop the added table");
                Assert.That(ticketsSeqGone, Is.True,
                    "DOWN must drop the added table's sequence too, after the table");
                Assert.That(nicknameGone, Is.True,
                    "DOWN must revert the column added to the existing table");

                // migration 1's schema is untouched
                Assert.That(usersKept, Is.True);
                Assert.That(emailKept, Is.True);
                Assert.That(historyKept, Is.True);
                Assert.That(historyRows, Is.EqualTo(3), "two applies and one rollback are recorded");
            });
        }
        finally { await Exec(conn, $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE;"); }
    }

    private static async Task<bool> ColumnExists(NpgsqlConnection c, string schema, string table, string column)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*) FROM information_schema.columns
                            WHERE table_schema = @s AND table_name = @t AND column_name = @c";
        cmd.Parameters.AddWithValue("s", schema);
        cmd.Parameters.AddWithValue("t", table);
        cmd.Parameters.AddWithValue("c", column);
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }
}
