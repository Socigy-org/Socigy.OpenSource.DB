using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using NUnit.Framework;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Introspection;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using static Socigy.OpenSource.DB.Tool.Tests.TestSchema;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Index DDL against a real PostgreSQL, in both directions: the SQL the generator emits has to apply and roll
/// back, and the schema reader has to recover what is in the database. Unit tests can only check the text of a
/// statement; only the server can confirm it is valid and that the reader's catalog queries return what they
/// claim to.
///
/// Gated on a reachable PostgreSQL (env <c>SOCIGY_TEST_PG</c>).
/// </summary>
[TestFixture]
public class IndexLiveTests
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

    private static async Task<NpgsqlConnection?> OpenInScratchSchemaAsync(string schema)
    {
        var conn = await TryOpenAsync();
        if (conn is null) return null;
        await Exec(conn, $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE; CREATE SCHEMA ""{schema}"";");
        await Exec(conn, $@"SET search_path TO ""{schema}"";");
        return conn;
    }

    /// <summary>Index definitions in the scratch schema, keyed by index name.</summary>
    private static async Task<Dictionary<string, string>> ReadIndexDefs(NpgsqlConnection c, string schema)
    {
        var defs = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = @s";
        cmd.Parameters.AddWithValue("s", schema);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) defs[r.GetString(0)] = r.GetString(1);
        return defs;
    }

    /// <summary>
    /// Index definitions excluding those backing a primary key or unique constraint. Those are modelled as
    /// constraints rather than indexes, and their names follow the constraint naming convention, so they are
    /// not what an index round-trip is about.
    /// </summary>
    private static async Task<Dictionary<string, string>> ReadStandaloneIndexDefs(NpgsqlConnection c, string schema)
    {
        var defs = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT i.relname, pg_get_indexdef(ix.indexrelid)
                            FROM pg_index ix
                            JOIN pg_class i     ON i.oid = ix.indexrelid
                            JOIN pg_class t     ON t.oid = ix.indrelid
                            JOIN pg_namespace n ON n.oid = t.relnamespace
                            WHERE n.nspname = @s
                              AND NOT ix.indisprimary
                              AND NOT EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conindid = ix.indexrelid)";
        cmd.Parameters.AddWithValue("s", schema);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) defs[r.GetString(0)] = r.GetString(1);
        return defs;
    }

    private static DbTable UsersTable(params DbIndex[] indexes)
    {
        var table = Table("users",
            Col("id", "uuid", pk: true),
            Col("tenant_id", "uuid"),
            Col("email", "text"),
            Col("status", "text", nullable: true));
        table.Indexes = indexes.ToList();
        return table;
    }

    private static DbIndex Index(params string[] columns) => new()
    {
        TableName = "users",
        Columns = columns,
    };

    // ── generated DDL has to be accepted by the server, and be reversible ──

    [Test]
    public async Task Generated_index_ddl_applies_and_rolls_back()
    {
        const string schema = "socigy_idx_apply";
        var conn = await OpenInScratchSchemaAsync(schema);
        if (conn is null) { Assert.Ignore("No reachable PostgreSQL (set SOCIGY_TEST_PG)."); return; }
        await using var _ = conn;
        try
        {
            var plain = Index("email");
            var unique = Index("tenant_id", "email");
            unique.IsUnique = true;
            var partial = Index("status");
            partial.Where = "status IS NOT NULL";
            partial.Name = "ix_live_status";
            var covering = Index("tenant_id");
            covering.IncludeColumns = ["email"];
            covering.DescendingColumns = ["tenant_id"];
            covering.NullsLastColumns = ["tenant_id"];
            var table = UsersTable(plain, unique, partial, covering);
            UseSchema(table);

            var (up, down) = new PostgreSqlGenerator()
                .Generate(new SchemaDiff { AddedTables = { table } }, isFirstMigration: false);

            await Exec(conn, string.Join("\n", up));

            var defs = await ReadIndexDefs(conn, schema);
            Assert.Multiple(() =>
            {
                Assert.That(defs.Keys, Has.Some.EqualTo("IX_users_email"));
                Assert.That(defs.Keys, Has.Some.EqualTo("ix_live_status"));
                Assert.That(defs["ix_live_status"], Does.Contain("WHERE"), "the partial filter must survive");
                Assert.That(defs.Values, Has.Some.Contains("UNIQUE INDEX"), "the unique index must be unique");
                Assert.That(defs.Values, Has.Some.Contains("INCLUDE"), "the covering column must survive");
                Assert.That(defs.Values, Has.Some.Contains("DESC"), "the sort order must survive");
            });

            // Rolling the table back takes its indexes with it; nothing may be left behind.
            await Exec(conn, string.Join("\n", down));
            var afterDown = await ReadIndexDefs(conn, schema);
            Assert.That(afterDown, Is.Empty, "the rollback must leave no index behind");
        }
        finally { await Exec(conn, $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE;"); }
    }

    [Test]
    public async Task Adding_and_dropping_an_index_on_an_existing_table_round_trips()
    {
        const string schema = "socigy_idx_alter";
        var conn = await OpenInScratchSchemaAsync(schema);
        if (conn is null) { Assert.Ignore("No reachable PostgreSQL (set SOCIGY_TEST_PG)."); return; }
        await using var _ = conn;
        try
        {
            var table = UsersTable();
            UseSchema(table);
            var (createTable, _) = new PostgreSqlGenerator()
                .Generate(new SchemaDiff { AddedTables = { table } }, isFirstMigration: false);
            await Exec(conn, string.Join("\n", createTable));

            var alteration = new TableAlteration { Table = table };
            alteration.ProvideDefaults();
            alteration.AddedIndexes.Add(Index("email"));

            var (up, down) = new PostgreSqlGenerator()
                .Generate(new SchemaDiff { AlteredTables = { alteration } }, isFirstMigration: false);

            await Exec(conn, string.Join("\n", up));
            Assert.That((await ReadIndexDefs(conn, schema)).Keys, Has.Some.EqualTo("IX_users_email"));

            await Exec(conn, string.Join("\n", down));
            Assert.That((await ReadIndexDefs(conn, schema)).Keys, Has.None.EqualTo("IX_users_email"),
                "the DOWN must drop exactly the index the UP created");
        }
        finally { await Exec(conn, $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE;"); }
    }

    // ── the reader has to recover what is actually in the database ──

    [Test]
    public async Task Schema_reader_recovers_indexes_and_their_options()
    {
        const string schema = "socigy_idx_read";
        var conn = await OpenInScratchSchemaAsync(schema);
        if (conn is null) { Assert.Ignore("No reachable PostgreSQL (set SOCIGY_TEST_PG)."); return; }
        await using var _ = conn;
        try
        {
            await Exec(conn, $@"
                CREATE TABLE ""{schema}"".""users"" (
                    ""id""        uuid PRIMARY KEY,
                    ""tenant_id"" uuid NOT NULL,
                    ""email""     text NOT NULL UNIQUE,
                    ""status""    text
                );
                CREATE INDEX ""ix_plain"" ON ""{schema}"".""users"" (""tenant_id"");
                CREATE UNIQUE INDEX ""ix_unique"" ON ""{schema}"".""users"" (""tenant_id"", ""email"");
                CREATE INDEX ""ix_partial"" ON ""{schema}"".""users"" (""status"") WHERE status IS NOT NULL;
                CREATE INDEX ""ix_covering"" ON ""{schema}"".""users"" (""tenant_id"") INCLUDE (""email"");
                CREATE INDEX ""ix_ordered"" ON ""{schema}"".""users"" (""email"" DESC NULLS LAST);
                CREATE INDEX ""ix_hash"" ON ""{schema}"".""users"" USING hash (""email"");
                CREATE INDEX ""ix_expression"" ON ""{schema}"".""users"" (lower(""email""));");

            var read = await PostgresSchemaReader.ReadAsync(ConnString(), schema);
            var indexes = (read.Tables.Single(t => t.Name == "users").Indexes ?? [])
                .ToDictionary(i => i.Name, StringComparer.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(indexes.Keys, Does.Contain("ix_plain"));
                Assert.That(indexes["ix_plain"].Columns, Is.EqualTo(new[] { "TenantId" }),
                    "columns come back as property names, ready for nameof() in the scaffolded class");

                Assert.That(indexes["ix_unique"].IsUnique, Is.True);
                Assert.That(indexes["ix_unique"].Columns, Is.EqualTo(new[] { "TenantId", "Email" }));

                Assert.That(indexes["ix_partial"].Where, Is.Not.Null.And.Contains("status"));
                Assert.That(indexes["ix_covering"].IncludeColumns, Is.EqualTo(new[] { "Email" }));

                Assert.That(indexes["ix_ordered"].DescendingColumns, Is.EqualTo(new[] { "Email" }));
                Assert.That(indexes["ix_ordered"].NullsLastColumns, Is.EqualTo(new[] { "Email" }));

                // The access method comes back as a portable intent token, not "hash".
                Assert.That(indexes["ix_hash"].Method, Is.EqualTo(DbIndexMethods.Hash));

                // An expression index has no [Index] form; it is reported and skipped rather than mis-read.
                Assert.That(indexes.Keys, Does.Not.Contain("ix_expression"));

                // The indexes backing the primary key and the UNIQUE constraint are already modelled as
                // constraints; reading them here too would emit a duplicate index on every generate.
                Assert.That(indexes.Values.Any(i => i.Columns.SequenceEqual(new[] { "Id" })), Is.False,
                    "the primary key's index must not be read as a standalone index");
                Assert.That(indexes.Values.Any(i => i.Columns.SequenceEqual(new[] { "Email" }) && i.IsUnique), Is.False,
                    "the UNIQUE constraint's index must not be read as a standalone index");
            });
        }
        finally { await Exec(conn, $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE;"); }
    }

    // The round trip that matters: what the reader recovers must regenerate to the same database state, so a
    // scaffolded project does not produce a migration churning its own indexes.
    [Test]
    public async Task Read_indexes_regenerate_to_the_same_database_state()
    {
        const string schema = "socigy_idx_roundtrip";
        var conn = await OpenInScratchSchemaAsync(schema);
        if (conn is null) { Assert.Ignore("No reachable PostgreSQL (set SOCIGY_TEST_PG)."); return; }
        await using var _ = conn;
        try
        {
            await Exec(conn, $@"
                CREATE TABLE ""{schema}"".""users"" (
                    ""id""        uuid PRIMARY KEY,
                    ""tenant_id"" uuid NOT NULL,
                    ""email""     text NOT NULL
                );
                CREATE UNIQUE INDEX ""ix_tenant_email"" ON ""{schema}"".""users"" (""tenant_id"", ""email"");
                CREATE INDEX ""ix_email"" ON ""{schema}"".""users"" (""email"" DESC);");

            var before = await ReadStandaloneIndexDefs(conn, schema);
            var read = await PostgresSchemaReader.ReadAsync(ConnString(), schema);
            var table = read.Tables.Single(t => t.Name == "users");

            // Drop and rebuild from what the reader recovered.
            await Exec(conn, $@"DROP TABLE ""{schema}"".""users"" CASCADE;");
            UseSchema(table);
            var (up, _unused) = new PostgreSqlGenerator()
                .Generate(new SchemaDiff { AddedTables = { table } }, isFirstMigration: false);
            await Exec(conn, string.Join("\n", up));

            var after = await ReadStandaloneIndexDefs(conn, schema);

            Assert.That(after.Keys, Is.EquivalentTo(before.Keys),
                "regenerating from a read schema must produce the same set of indexes");
            foreach (var name in before.Keys)
                Assert.That(Normalize(after[name]), Is.EqualTo(Normalize(before[name])),
                    $"index \"{name}\" must be recreated with the same definition");
        }
        finally { await Exec(conn, $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE;"); }
    }

    // pg_indexes renders the definition canonically, so only whitespace and the schema qualification differ.
    private static string Normalize(string definition) =>
        string.Join(" ", definition.Replace("\"", "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
}
