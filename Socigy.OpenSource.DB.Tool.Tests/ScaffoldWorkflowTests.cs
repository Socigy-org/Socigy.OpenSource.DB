using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using NUnit.Framework;
using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Introspection;
using Socigy.OpenSource.DB.Tool.Scaffolding;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// End-to-end coverage for the database-first workflows: live DB → schema (DbSchema) → C# classes, and a
/// schema round-trip (DB → DbSchema → generated DDL → DB → DbSchema) that proves the inverse (read) and
/// forward (generate) translators agree. Gated on a reachable PostgreSQL (env <c>SOCIGY_TEST_PG</c>).
/// </summary>
[TestFixture]
public class ScaffoldWorkflowTests
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

    // ── live DB → schema → C# classes: the emitted source has the right types and attributes ──
    [Test]
    public async Task LiveDb_To_Classes_emits_expected_types_and_attributes()
    {
        var conn = await TryOpenAsync();
        if (conn is null) { Assert.Ignore("No reachable PostgreSQL (set SOCIGY_TEST_PG)."); return; }
        await using var _ = conn;
        const string schema = "socigy_wf_classes";
        await Exec(conn, $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE; CREATE SCHEMA ""{schema}"";");
        try
        {
            await Exec(conn, $@"
                CREATE TABLE ""{schema}"".""widget"" (
                    ""id""       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    ""name""     varchar(100) NOT NULL,
                    ""count""    integer NOT NULL DEFAULT 0,
                    ""price""    numeric,
                    ""active""   boolean NOT NULL DEFAULT true,
                    ""created""  timestamp without time zone DEFAULT timezone('utc', now()),
                    ""moment""   timestamp with time zone,
                    ""the_day""  date,
                    ""the_time"" time without time zone,
                    ""span""     interval,
                    ""blob""     bytea,
                    ""payload""  jsonb,
                    ""big""      bigint,
                    ""small""    smallint,
                    ""ratio""    double precision,
                    ""amount""   real
                );");

            var dbSchema = await PostgresSchemaReader.ReadAsync(ConnString(), schema);
            var files = CSharpClassEmitter.Emit(dbSchema, "Gen.Models");
            Assert.That(files.ContainsKey("Widget.cs"), "one file per table, named after the PascalCase table");
            string src = files["Widget.cs"];

            Assert.Multiple(() =>
            {
                Assert.That(src, Does.Contain("[Table(\"widget\")]"));
                Assert.That(src, Does.Contain("public partial class Widget"));
                Assert.That(src, Does.Contain("public Guid Id"));
                Assert.That(src, Does.Contain("[PrimaryKey"));
                Assert.That(src, Does.Contain("[StringLength(100)]"));
                Assert.That(src, Does.Contain("public string Name"));
                Assert.That(src, Does.Contain("public int Count"));
                Assert.That(src, Does.Contain("public decimal? Price"));
                Assert.That(src, Does.Contain("public bool Active"));
                Assert.That(src, Does.Contain("public DateTime? Created"));
                Assert.That(src, Does.Contain("public DateTimeOffset? Moment"));
                Assert.That(src, Does.Contain("public DateOnly? TheDay"));
                Assert.That(src, Does.Contain("public TimeOnly? TheTime"));
                Assert.That(src, Does.Contain("public TimeSpan? Span"));
                Assert.That(src, Does.Contain("public byte[]? Blob"));
                Assert.That(src, Does.Contain("[RawJsonColumn]"));
                Assert.That(src, Does.Contain("public string? Payload"));
                Assert.That(src, Does.Contain("public long? Big"));
                Assert.That(src, Does.Contain("public short? Small"));
                Assert.That(src, Does.Contain("public double? Ratio"));
                Assert.That(src, Does.Contain("public float? Amount"));
            });
        }
        finally { await Exec(conn, $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE;"); }
    }

    // ── round-trip: DB → DbSchema → forward-generated DDL → DB → DbSchema, compare structurally ──
    [Test]
    public async Task Schema_round_trips_through_generated_ddl()
    {
        var conn = await TryOpenAsync();
        if (conn is null) { Assert.Ignore("No reachable PostgreSQL (set SOCIGY_TEST_PG)."); return; }
        await using var _ = conn;
        const string srcS = "socigy_wf_src";
        const string dstS = "socigy_wf_dst";
        await Exec(conn, $@"DROP SCHEMA IF EXISTS ""{srcS}"" CASCADE; CREATE SCHEMA ""{srcS}"";
                            DROP SCHEMA IF EXISTS ""{dstS}"" CASCADE; CREATE SCHEMA ""{dstS}"";");
        try
        {
            await Exec(conn, $@"
                CREATE TABLE ""{srcS}"".""parent"" (
                    ""id""    uuid NOT NULL,
                    ""name""  varchar(40) NOT NULL,
                    ""score"" integer NOT NULL DEFAULT 0,
                    ""price"" numeric(10,2),
                    ""flag""  boolean,
                    ""ts""    timestamp without time zone,
                    CONSTRAINT ""pk_parent"" PRIMARY KEY (""id"")
                );
                CREATE TABLE ""{srcS}"".""child"" (
                    ""id""        bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    ""parent_id"" uuid NOT NULL REFERENCES ""{srcS}"".""parent""(""id"") ON DELETE CASCADE,
                    ""label""     text
                );");

            var schema1 = await PostgresSchemaReader.ReadAsync(ConnString(), srcS);

            // Scaffolding must capture numeric precision/scale (not collapse to a bare "numeric").
            var price = schema1.Tables.Single(t => t.Name == "parent").Columns.Single(c => c.Name == "price");
            Assert.That(price.DatabaseType, Is.EqualTo("numeric(10,2)"), "numeric precision/scale must be preserved");

            // The forward generator resolves FK targets/columns through Configuration.CurrentSchema (set by the
            // CLI's `generate` command); mirror that here before generating from the scaffolded schema.
            Socigy.OpenSource.DB.Tool.Configuration.CurrentSchema = schema1;
            var diff = new SchemaDiff { AddedTables = schema1.Tables.ToList() };
            var (up, _) = new PostgreSqlGenerator().Generate(diff, isFirstMigration: true);

            await Exec(conn, $@"SET search_path TO ""{dstS}"";");
            foreach (var stmt in up)
                if (!string.IsNullOrWhiteSpace(stmt) && !stmt.TrimStart().StartsWith("--"))
                    await Exec(conn, stmt);
            await Exec(conn, "RESET search_path;");

            var schema2 = await PostgresSchemaReader.ReadAsync(ConnString(), dstS);

            Assert.That(schema2.Tables.Select(t => t.Name), Is.EquivalentTo(schema1.Tables.Select(t => t.Name)));
            foreach (var t1 in schema1.Tables)
            {
                var t2 = schema2.Tables.Single(t => t.Name == t1.Name);
                Assert.That(t2.Columns.Select(c => c.Name), Is.EquivalentTo(t1.Columns.Select(c => c.Name)), $"{t1.Name} columns");
                foreach (var c1 in t1.Columns)
                {
                    var c2 = t2.Columns.Single(c => c.Name == c1.Name);
                    string ctx = $"{t1.Name}.{c1.Name}";
                    Assert.That(c2.DotnetType, Is.EqualTo(c1.DotnetType), $"{ctx} dotnet type");
                    Assert.That(c2.DatabaseType, Is.EqualTo(c1.DatabaseType), $"{ctx} db type (round-trip fidelity)");
                    Assert.That(c2.Nullable, Is.EqualTo(c1.Nullable), $"{ctx} nullable");
                    Assert.That(c2.IsPrimaryKey ?? false, Is.EqualTo(c1.IsPrimaryKey ?? false), $"{ctx} primary key");
                    Assert.That(c2.IsAutoIncrement ?? false, Is.EqualTo(c1.IsAutoIncrement ?? false), $"{ctx} auto-increment");
                    Assert.That(c2.MaxLength, Is.EqualTo(c1.MaxLength), $"{ctx} max length");
                    Assert.That(c2.DefaultValue, Is.EqualTo(c1.DefaultValue), $"{ctx} default");
                }

                var fk1 = t1.Constraints.Where(c => c.Type == DbConstraint.Types.ForeignKey).ToList();
                var fk2 = t2.Constraints.Where(c => c.Type == DbConstraint.Types.ForeignKey).ToList();
                Assert.That(fk2.Count, Is.EqualTo(fk1.Count), $"{t1.Name} fk count");
                if (fk1.Count == 1)
                {
                    Assert.That(fk2[0].TargetTable, Is.EqualTo(fk1[0].TargetTable), $"{t1.Name} fk target");
                    Assert.That(fk2[0].Columns, Is.EquivalentTo(fk1[0].Columns), $"{t1.Name} fk columns");
                    Assert.That(fk2[0].OnDelete, Is.EqualTo(fk1[0].OnDelete), $"{t1.Name} fk on delete");
                }
            }
        }
        finally { await Exec(conn, $@"DROP SCHEMA IF EXISTS ""{srcS}"" CASCADE; DROP SCHEMA IF EXISTS ""{dstS}"" CASCADE;"); }
    }
}
