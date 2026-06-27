using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using static Socigy.OpenSource.DB.Tool.Tests.TestSchema;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>Regression tests for schema-diff DDL generation bugs found in the 6th audit pass.</summary>
[TestFixture]
public class MigrationGeneratorFixTests
{
    // A changed [Default] emitted the raw $socigy$ token (e.g. "SET DEFAULT $socigy$guid.random"), which
    // fails at apply ("unterminated dollar-quoted string"). It must be translated like the CREATE/ADD paths.
    [Test]
    public void Default_change_translates_socigy_token()
    {
        var table = Table("t_def", Col("id", "uuid", pk: true, defaultValue: "$socigy$guid.random"));
        UseSchema(table);
        var alt = new TableAlteration { Table = table };
        alt.ProvideDefaults();
        alt.ModifiedColumns.Add(new ColumnAlteration
        {
            OldColumn = Col("id", "uuid", pk: true, defaultValue: null),
            NewColumn = Col("id", "uuid", pk: true, defaultValue: "$socigy$guid.random"),
            Changes = { "Default" },
        });

        var (up, _) = new PostgreSqlGenerator().Generate(new SchemaDiff { AlteredTables = { alt } }, false);
        var upText = string.Join("\n", up);
        Assert.Multiple(() =>
        {
            Assert.That(upText, Does.Not.Contain("$socigy$"), "the token must be translated, not emitted verbatim");
            Assert.That(upText, Does.Contain("SET DEFAULT").And.Contain("gen_random_uuid()"));
        });
    }

    // A primary-key change dropped the new PK in DOWN but never re-added the OLD one, leaving the table with
    // no primary key after a rollback. DOWN must restore the prior key.
    [Test]
    public void Primary_key_change_down_restores_old_pk()
    {
        // New PK is (id, code); old PK was just (id).
        var table = Table("orders", Col("id", "uuid", pk: true), Col("code", "text", pk: true));
        UseSchema(table);
        var alt = new TableAlteration { Table = table };
        alt.ProvideDefaults();
        alt.ModifiedColumns.Add(new ColumnAlteration
        {
            OldColumn = Col("code", "text", pk: false),
            NewColumn = Col("code", "text", pk: true),
            Changes = { "PrimaryKey" },
        });

        var (_, down) = new PostgreSqlGenerator().Generate(new SchemaDiff { AlteredTables = { alt } }, false);
        var downText = string.Join("\n", down);
        Assert.That(downText, Does.Contain("ADD CONSTRAINT").And.Contain("PRIMARY KEY (\"id\")"),
            "DOWN must restore the old primary key (id), not leave the table without one");
    }

    // The analyzer marks a non-nullable column Nullable==null (never false); the CREATE TABLE generator must still
    // emit NOT NULL for it, else every required, non-primary-key column is created NULLABLE.
    [Test]
    public void Create_table_emits_not_null_for_non_nullable_columns()
    {
        var table = new DbTable
        {
            Name = "courses",
            SourceName = "courses",
            Constraints = new List<DbConstraint>(),
            Columns = new List<DbColumn>
            {
                new() { Name = "id", SourceName = "id", DatabaseType = "uuid", IsPrimaryKey = true, Nullable = null },
                new() { Name = "name", SourceName = "name", DatabaseType = "text", Nullable = null },   // non-nullable
                new() { Name = "note", SourceName = "note", DatabaseType = "text", Nullable = true },    // nullable
            },
        };
        UseSchema(table);

        var (up, _) = new PostgreSqlGenerator().Generate(new SchemaDiff { AddedTables = { table } }, false);
        var upText = string.Join("\n", up);

        Assert.Multiple(() =>
        {
            Assert.That(upText, Does.Match("\"name\"\\s+text\\s+NOT NULL"), "a non-nullable column must be created NOT NULL");
            Assert.That(upText, Does.Not.Match("\"note\"\\s+text\\s+NOT NULL"), "a nullable column must not be NOT NULL");
        });
    }

    // Adding an [AutoIncrement] column via ALTER must create a sequence named with the table and reference that
    // SAME name in the column default (the bug used a null table name -> "_counter_seq", a sequence never created,
    // so UP failed at apply). The DOWN must drop the column before the sequence it depends on.
    [Test]
    public void Added_auto_increment_column_references_table_sequence_and_down_drops_in_order()
    {
        var table = Table("widgets", Col("id", "uuid", pk: true),
            Col("counter", "integer", autoIncrement: true, dotnetType: "int"));
        UseSchema(table);
        var alt = new TableAlteration { Table = table };
        alt.ProvideDefaults();
        alt.AddedColumns.Add(Col("counter", "integer", autoIncrement: true, dotnetType: "int"));

        var (up, down) = new PostgreSqlGenerator().Generate(new SchemaDiff { AlteredTables = { alt } }, false);
        var upText = string.Join("\n", up);
        var downText = string.Join("\n", down);

        Assert.Multiple(() =>
        {
            Assert.That(upText, Does.Contain("CREATE SEQUENCE IF NOT EXISTS \"widgets_counter_seq\"").And.Contain("AS INTEGER"));
            Assert.That(upText, Does.Contain("nextval('widgets_counter_seq')"));
            Assert.That(upText, Does.Not.Contain("nextval('_counter_seq')"), "must not reference an un-prefixed sequence name");

            int dropCol = downText.IndexOf("DROP COLUMN", StringComparison.Ordinal);
            int dropSeq = downText.IndexOf("DROP SEQUENCE", StringComparison.Ordinal);
            Assert.That(dropCol, Is.GreaterThanOrEqualTo(0), "DOWN must drop the column");
            Assert.That(dropSeq, Is.GreaterThan(dropCol), "DOWN must drop the column before the sequence it depends on");
        });
    }

    // A removed UNIQUE/FK constraint whose column can't be matched to a current-table column (e.g. the column
    // was renamed) must re-add on the snake_case column name in DOWN, not the raw PascalCase property name,
    // which is never a valid identifier and fails at apply.
    [Test]
    public void Removed_constraint_down_uses_snake_case_column_when_unresolved()
    {
        // New schema has the column renamed (phone_number); the old UNIQUE still references "Phone".
        var table = Table("users", Col("id", "uuid", pk: true), Col("phone_number", "text"));
        UseSchema(table);
        var alt = new TableAlteration { Table = table };
        alt.ProvideDefaults();
        alt.RemovedConstraints.Add(new DbConstraint
        {
            Type = DbConstraint.Types.Unique,
            TableName = "users",
            Columns = new[] { "Phone" }, // old property name, no longer resolvable in the new schema
        });

        var (_, down) = new PostgreSqlGenerator().Generate(new SchemaDiff { AlteredTables = { alt } }, false);
        var downText = string.Join("\n", down);
        Assert.Multiple(() =>
        {
            Assert.That(downText, Does.Contain("UNIQUE (\"phone\")"), "DOWN re-adds the constraint on the snake_case column");
            Assert.That(downText, Does.Not.Contain("\"Phone\""), "must not emit the raw PascalCase property name");
        });
    }
}
