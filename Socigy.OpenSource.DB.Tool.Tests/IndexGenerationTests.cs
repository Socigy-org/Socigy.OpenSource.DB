using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using static Socigy.OpenSource.DB.Tool.Tests.TestSchema;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Index DDL emitted by the PostgreSQL generator, and where it lands relative to the rest of a migration.
/// Ordering matters as much as syntax: an index needs its table and columns to exist, and a redefined index
/// has to be dropped before it is recreated.
/// </summary>
[TestFixture]
public class IndexGenerationTests
{
    private static DbIndex Index(string table, params string[] columns) => new()
    {
        TableName = table,
        Columns = columns,
    };

    private static (string Up, string Down) Generate(SchemaDiff diff)
    {
        var (up, down) = new PostgreSqlGenerator().Generate(diff, isFirstMigration: false);
        return (string.Join("\n", up), string.Join("\n", down));
    }

    private static DbTable Users(params DbIndex[] indexes)
    {
        var table = Table("users", Col("id", "uuid", pk: true), Col("email", "text"), Col("status", "text"));
        table.Indexes = indexes.ToList();
        return table;
    }

    // ── new tables ──

    [Test]
    public void Added_table_creates_its_indexes_after_the_table()
    {
        var table = Users(Index("users", "email"));
        UseSchema(table);

        var (up, down) = Generate(new SchemaDiff { AddedTables = { table } });

        int createTable = up.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        int createIndex = up.IndexOf("CREATE INDEX", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(createIndex, Is.GreaterThan(createTable), "there must be a table to index first");
            Assert.That(up, Does.Contain("CREATE INDEX IF NOT EXISTS \"IX_users_email\" ON \"users\" (\"email\")"));
            Assert.That(down, Does.Not.Contain("DROP INDEX"),
                "dropping the table drops its indexes, so an explicit DROP INDEX would be redundant");
        });
    }

    [Test]
    public void Removed_table_recreates_its_indexes_in_the_down()
    {
        var table = Users(Index("users", "email"));
        UseSchema();

        var (up, down) = Generate(new SchemaDiff { RemovedTables = { table } });

        int createTable = down.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        int createIndex = down.IndexOf("CREATE INDEX", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(up, Does.Not.Contain("DROP INDEX"), "the DROP TABLE already took them");
            Assert.That(createIndex, Is.GreaterThan(createTable),
                "the rollback has to put the table back before it can index it");
        });
    }

    // ── altering an existing table ──

    [Test]
    public void Added_index_is_created_last_and_dropped_in_the_down()
    {
        var table = Users();
        UseSchema(table);
        var alteration = new TableAlteration { Table = table };
        alteration.ProvideDefaults();
        alteration.AddedColumns.Add(Col("nickname", "text", nullable: true));
        alteration.AddedIndexes.Add(Index("users", "nickname"));

        var (up, down) = Generate(new SchemaDiff { AlteredTables = { alteration } });

        int addColumn = up.IndexOf("ADD COLUMN", StringComparison.Ordinal);
        int createIndex = up.IndexOf("CREATE INDEX", StringComparison.Ordinal);
        int dropIndex = down.IndexOf("DROP INDEX", StringComparison.Ordinal);
        int dropColumn = down.IndexOf("DROP COLUMN", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(createIndex, Is.GreaterThan(addColumn),
                "the column the index covers has to exist first");
            Assert.That(dropIndex, Is.GreaterThanOrEqualTo(0).And.LessThan(dropColumn),
                "the rollback drops the index before the column it covers");
        });
    }

    [Test]
    public void Removed_index_is_dropped_first_and_recreated_in_the_down()
    {
        var table = Users();
        UseSchema(table);
        var alteration = new TableAlteration { Table = table };
        alteration.ProvideDefaults();
        alteration.RemovedColumns.Add(Col("nickname", "text", nullable: true));
        alteration.RemovedIndexes.Add(Index("users", "nickname"));

        var (up, down) = Generate(new SchemaDiff { AlteredTables = { alteration } });

        int dropIndex = up.IndexOf("DROP INDEX", StringComparison.Ordinal);
        int dropColumn = up.IndexOf("DROP COLUMN", StringComparison.Ordinal);
        int addColumn = down.IndexOf("ADD COLUMN", StringComparison.Ordinal);
        int createIndex = down.IndexOf("CREATE INDEX", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(dropIndex, Is.GreaterThanOrEqualTo(0).And.LessThan(dropColumn),
                "the index goes before the column it covers");
            Assert.That(createIndex, Is.GreaterThan(addColumn),
                "the rollback re-adds the column before rebuilding the index over it");
        });
    }

    // A redefinition arrives as a removal plus an addition of the same name; emitting CREATE before DROP
    // would fail with "already exists".
    [Test]
    public void Redefined_index_drops_before_it_creates()
    {
        var table = Users();
        UseSchema(table);

        var oldIndex = Index("users", "email");
        oldIndex.Name = "ix_email";
        var newIndex = Index("users", "email");
        newIndex.Name = "ix_email";
        newIndex.IsUnique = true;

        var alteration = new TableAlteration { Table = table };
        alteration.ProvideDefaults();
        alteration.RemovedIndexes.Add(oldIndex);
        alteration.AddedIndexes.Add(newIndex);

        var (up, _) = Generate(new SchemaDiff { AlteredTables = { alteration } });

        Assert.That(up.IndexOf("DROP INDEX", StringComparison.Ordinal),
            Is.LessThan(up.IndexOf("CREATE UNIQUE INDEX", StringComparison.Ordinal)));
    }

    [Test]
    public void Dropping_an_index_raises_a_safety_warning()
    {
        var table = Users();
        UseSchema(table);
        var alteration = new TableAlteration { Table = table };
        alteration.ProvideDefaults();
        alteration.RemovedIndexes.Add(Index("users", "email"));

        var generator = new PostgreSqlGenerator();
        generator.Generate(new SchemaDiff { AlteredTables = { alteration } }, false);

        Assert.That(generator.SafetyWarnings, Has.Some.Contains("IX_users_email"),
            "rebuilding an index on a large table is expensive enough to flag before it is applied");
    }

    // ── rendering ──

    [Test]
    public void Unique_index_is_rendered_unique()
    {
        var index = Index("users", "email");
        index.IsUnique = true;
        var table = Users(index);
        UseSchema(table);

        var (up, _) = Generate(new SchemaDiff { AddedTables = { table } });

        Assert.That(up, Does.Contain("CREATE UNIQUE INDEX IF NOT EXISTS \"UX_users_email\""));
    }

    [TestCase(DbIndexMethods.Hash, "hash")]
    [TestCase(DbIndexMethods.FullText, "gin")]
    [TestCase(DbIndexMethods.Spatial, "gist")]
    [TestCase(DbIndexMethods.Contains, "gin")]
    [TestCase(DbIndexMethods.BlockRange, "brin")]
    public void Method_intent_is_translated_to_the_postgres_access_method(string token, string expected)
    {
        var index = Index("users", "email");
        index.Method = token;
        var table = Users(index);
        UseSchema(table);

        var (up, _) = Generate(new SchemaDiff { AddedTables = { table } });

        Assert.That(up, Does.Contain($"USING {expected} (\"email\")"));
    }

    [Test]
    public void Default_method_emits_no_using_clause()
    {
        var index = Index("users", "email");
        index.Method = DbIndexMethods.Default;
        var table = Users(index);
        UseSchema(table);

        var (up, _) = Generate(new SchemaDiff { AddedTables = { table } });

        Assert.That(up, Does.Not.Contain("USING"), "btree is the default and needs no USING clause");
    }

    [Test]
    public void Raw_method_overrides_the_intent_token()
    {
        var index = Index("users", "email");
        index.Method = DbIndexMethods.Hash;
        index.RawMethod = "gist";
        var table = Users(index);
        UseSchema(table);

        var (up, _) = Generate(new SchemaDiff { AddedTables = { table } });

        Assert.Multiple(() =>
        {
            Assert.That(up, Does.Contain("USING gist"));
            Assert.That(up, Does.Not.Contain("USING hash"));
        });
    }

    [Test]
    public void Filter_include_and_ordering_are_rendered()
    {
        var index = Index("users", "email");
        index.Where = "status <> 'deleted'";
        index.IncludeColumns = ["status"];
        index.DescendingColumns = ["email"];
        index.NullsLastColumns = ["email"];
        var table = Users(index);
        UseSchema(table);

        var (up, _) = Generate(new SchemaDiff { AddedTables = { table } });

        Assert.That(up, Does.Contain("(\"email\" DESC NULLS LAST)")
                            .And.Contain("INCLUDE (\"status\")")
                            .And.Contain("WHERE status <> 'deleted'"));
    }

    // Index columns are stored as PROPERTY names, exactly like constraint columns, and must resolve to the
    // mapped database column name rather than a naive snake_case of the property.
    [Test]
    public void Property_names_resolve_to_mapped_column_names()
    {
        var col = Col("email_address", "text");
        col.SourceName = "Fixture.User.Email";
        var table = Table("users", Col("id", "uuid", pk: true), col);
        table.Indexes = [Index("users", "Email")];
        UseSchema(table);

        var (up, _) = Generate(new SchemaDiff { AddedTables = { table } });

        Assert.That(up, Does.Contain("(\"email_address\")").And.Contain("\"IX_users_email_address\""));
    }
}
