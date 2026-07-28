using Socigy.OpenSource.DB.Core.Migrations;
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

    // A newly CREATED table with an [AutoIncrement] column must be dropped BEFORE its sequence in DOWN. The
    // column's DEFAULT nextval(...) makes the table depend on the sequence and DROP TABLE ... CASCADE does not
    // cover it, so the reverse order fails with "cannot drop sequence ... other objects depend on it".
    [Test]
    public void Added_table_down_drops_table_before_its_sequence()
    {
        var table = Table("tickets",
            Col("id", "bigint", pk: true, autoIncrement: true, dotnetType: "System.Int64"),
            Col("label", "text"));
        UseSchema(table);

        var (up, down) = new PostgreSqlGenerator().Generate(new SchemaDiff { AddedTables = { table } }, false);
        var upText = string.Join("\n", up);
        var downText = string.Join("\n", down);

        int dropTable = downText.IndexOf("DROP TABLE", StringComparison.Ordinal);
        int dropSeq = downText.IndexOf("DROP SEQUENCE", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(upText, Does.Contain("CREATE SEQUENCE IF NOT EXISTS \"tickets_id_seq\""));
            Assert.That(dropTable, Is.GreaterThanOrEqualTo(0), "DOWN must drop the table");
            Assert.That(dropSeq, Is.GreaterThan(dropTable),
                "DOWN must drop the table before the sequence its DEFAULT depends on");
        });
    }

    // Dropping a table must also drop the sequence it owns (otherwise it is orphaned and a later re-add
    // silently resumes its old ids), and the sequence must go AFTER the table it belongs to.
    [Test]
    public void Removed_table_up_drops_its_sequence_after_the_table()
    {
        var table = Table("tickets",
            Col("id", "bigint", pk: true, autoIncrement: true, dotnetType: "System.Int64"),
            Col("label", "text"));
        // The dropped table is gone from the new schema; nothing else references its sequence.
        UseSchema();

        var (up, _) = new PostgreSqlGenerator().Generate(new SchemaDiff { RemovedTables = { table } }, false);
        var upText = string.Join("\n", up);

        int dropTable = upText.IndexOf("DROP TABLE", StringComparison.Ordinal);
        int dropSeq = upText.IndexOf("DROP SEQUENCE", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(dropTable, Is.GreaterThanOrEqualTo(0), "UP must drop the table");
            Assert.That(dropSeq, Is.GreaterThan(dropTable),
                "UP must drop the owned sequence after the table whose DEFAULT depends on it");
            Assert.That(upText, Does.Contain("DROP SEQUENCE IF EXISTS \"tickets_id_seq\""));
        });
    }

    // A sequence named explicitly via [AutoIncrement(SequenceName = "...")] can be shared. Dropping one of
    // the tables must NOT drop the sequence out from under the survivor's column default.
    [Test]
    public void Removed_table_keeps_a_sequence_another_table_still_uses()
    {
        var shared = new DbColumn
        {
            Name = "id", SourceName = "id", DatabaseType = "bigint", DotnetType = "System.Int64",
            IsPrimaryKey = true, IsAutoIncrement = true, SequenceName = "shared_id_seq",
        };
        var dropped = Table("tickets", shared, Col("label", "text"));
        var survivor = Table("invoices", shared, Col("total", "numeric"));
        // Only the survivor remains in the new schema.
        UseSchema(survivor);

        var generator = new PostgreSqlGenerator();
        var (up, _) = generator.Generate(new SchemaDiff { RemovedTables = { dropped } }, false);
        var upText = string.Join("\n", up);

        Assert.Multiple(() =>
        {
            Assert.That(upText, Does.Contain("DROP TABLE IF EXISTS \"tickets\""), "the table is still dropped");
            Assert.That(upText, Does.Not.Contain("DROP SEQUENCE"),
                "a sequence another table still uses must survive the drop");
            Assert.That(generator.SafetyWarnings, Has.Some.Contains("shared_id_seq").And.Some.Contains("invoices"),
                "the skipped drop must be surfaced as a safety warning");
        });
    }

    // Rolling back a DROPPED table recreates it with DEFAULT nextval(...), so the sequence must be recreated
    // FIRST, otherwise the CREATE TABLE references a sequence the UP just dropped.
    [Test]
    public void Removed_table_down_recreates_its_sequence_before_the_table()
    {
        var table = Table("tickets",
            Col("id", "bigint", pk: true, autoIncrement: true, dotnetType: "System.Int64"),
            Col("label", "text"));
        UseSchema();

        var (_, down) = new PostgreSqlGenerator().Generate(new SchemaDiff { RemovedTables = { table } }, false);
        var downText = string.Join("\n", down);

        int createSeq = downText.IndexOf("CREATE SEQUENCE", StringComparison.Ordinal);
        int createTable = downText.IndexOf("CREATE TABLE", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(createSeq, Is.GreaterThanOrEqualTo(0), "DOWN must recreate the sequence");
            Assert.That(createTable, Is.GreaterThan(createSeq),
                "DOWN must recreate the sequence before the table whose DEFAULT references it");
            Assert.That(downText, Does.Contain("nextval('tickets_id_seq')"));
        });
    }

    // The migration bookkeeping table is created by the first migration's UP, but its DOWN must never drop it:
    // the executor writes the IsRollback row into it in the same transaction, so dropping it makes the root
    // migration impossible to roll back. User tables in the same migration must still be dropped.
    [Test]
    public void First_migration_down_never_drops_the_migration_history_table()
    {
        var history = Table(MigrationHistory.TableName,
            Col("id", "bigint", pk: true, autoIncrement: true, dotnetType: "System.Int64"),
            Col("human_id", "text"));
        var users = Table("users", Col("id", "uuid", pk: true));
        UseSchema(history, users);

        var (up, down) = new PostgreSqlGenerator()
            .Generate(new SchemaDiff { AddedTables = { users, history } }, isFirstMigration: true);
        var upText = string.Join("\n", up);
        var downText = string.Join("\n", down);

        Assert.Multiple(() =>
        {
            Assert.That(upText, Does.Contain($"CREATE SEQUENCE IF NOT EXISTS \"{MigrationHistory.TableName}_id_seq\""),
                "UP still bootstraps the history sequence");
            Assert.That(upText, Does.Contain($"CREATE TABLE IF NOT EXISTS \"{MigrationHistory.TableName}\""),
                "UP still bootstraps the history table, guarded so it survives a rollback-then-reapply");

            Assert.That(downText, Does.Not.Contain($"DROP TABLE IF EXISTS \"{MigrationHistory.TableName}\""),
                "DOWN must leave the history table standing");
            Assert.That(downText, Does.Not.Contain($"DROP SEQUENCE IF EXISTS \"{MigrationHistory.TableName}_id_seq\""),
                "DOWN must leave the history sequence standing");
            Assert.That(downText, Does.Contain("DROP TABLE IF EXISTS \"users\""),
                "DOWN must still drop the user tables the migration created");
        });
    }

    // Because the DOWN deliberately leaves the history table standing, rolling the first migration back and
    // then forward again re-runs its CREATE against a table that still exists. The sequence beside it was
    // already guarded; the table was not, so the roll-forward failed with 42P07.
    [Test]
    public void First_migration_up_can_be_re_applied_after_a_rollback()
    {
        var history = Table(MigrationHistory.TableName,
            Col("id", "bigint", pk: true, autoIncrement: true, dotnetType: "System.Int64"),
            Col("human_id", "text"));
        var users = Table("users", Col("id", "uuid", pk: true));
        UseSchema(history, users);

        var (up, _) = new PostgreSqlGenerator()
            .Generate(new SchemaDiff { AddedTables = { users, history } }, isFirstMigration: true);
        var upText = string.Join("\n", up);

        Assert.Multiple(() =>
        {
            Assert.That(upText, Does.Contain($"CREATE TABLE IF NOT EXISTS \"{MigrationHistory.TableName}\""),
                "the one table the DOWN keeps must be re-creatable");
            Assert.That(upText, Does.Contain($"CREATE SEQUENCE IF NOT EXISTS \"{MigrationHistory.TableName}_id_seq\""),
                "its sequence is kept too and was already guarded");
            Assert.That(upText, Does.Contain("CREATE TABLE \"users\""),
                "a user table stays unguarded: one that already exists is a real conflict");
        });
    }
}
