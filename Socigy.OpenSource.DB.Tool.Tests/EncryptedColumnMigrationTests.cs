using System.Linq;
using NUnit.Framework;
using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using static Socigy.OpenSource.DB.Tool.Tests.TestSchema;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Migrations that touch <c>[Encrypted]</c>.
///
/// Encryption is the one column trait a SQL migration fundamentally cannot carry out, because only the
/// application holds the key. Two changes previously fell through the generic column machinery with results
/// that were worse than doing nothing:
///
/// <list type="bullet">
/// <item>Adding <c>[Encrypted]</c> to an existing <c>text</c> column emitted the generic in-place cast
/// <c>USING "col"::bytea</c>. That is an I/O-conversion cast: it aborts on any value containing a backslash,
/// and where it does succeed it stores readable plaintext in a column the model thereafter reports as
/// encrypted.</item>
/// <item>Changing a column's encryption <i>profile</i> produced nothing at all — no statement, no warning, no
/// comment — because both sides are <c>bytea</c>.</item>
/// </list>
///
/// The contract: refuse the first, make the second visible.
/// </summary>
[TestFixture]
public class EncryptedColumnMigrationTests
{
    private static DbColumn Encrypted(string name, string profile = null) => new()
    {
        Name = name,
        SourceName = "Body",
        DatabaseType = "bytea",
        IsEncrypted = true,
        EncryptionProfile = profile,
    };

    private static DbColumn Plaintext(string name, string type = "text") => new()
    {
        Name = name,
        SourceName = "Body",
        DatabaseType = type,
        IsEncrypted = false,
    };

    private static (PostgreSqlGenerator Generator, string Up, string Down) Generate(ColumnAlteration mod)
    {
        var table = Table("notes", Col("id", "uuid", pk: true), mod.NewColumn);
        UseSchema(table);

        var alteration = new TableAlteration { Table = table };
        alteration.ProvideDefaults();
        alteration.ModifiedColumns.Add(mod);

        var generator = new PostgreSqlGenerator();
        var (up, down) = generator.Generate(new SchemaDiff { AlteredTables = { alteration } }, isFirstMigration: false);
        return (generator, string.Join("\n", up), string.Join("\n", down));
    }

    // ── encrypting an existing plaintext column ──

    [TestCase("text")]
    [TestCase("character varying(200)")]
    public void Encrypting_an_existing_plaintext_column_is_refused(string oldType)
    {
        var (generator, up, _) = Generate(new ColumnAlteration
        {
            OldColumn = Plaintext("body", oldType),
            NewColumn = Encrypted("body"),
            Changes = { "Type", "EncryptionProfile" },
        });

        Assert.Multiple(() =>
        {
            Assert.That(generator.BlockingIssues, Is.Not.Empty,
                "there is no correct SQL for this change, so no migration should be produced at all");
            Assert.That(up, Does.Not.Contain("::bytea"),
                "the in-place cast cannot succeed and must not be emitted");

            var issue = string.Join("\n", generator.BlockingIssues);
            Assert.That(issue, Does.Contain("body"), "the message must name the column");
            Assert.That(issue, Does.Contain("notes"), "and its table");
        });
    }

    // The reverse (decrypting) is equally impossible — the plaintext only exists after the application has
    // decrypted it — and it silently passed IsSafeWidening because anything -> text was treated as safe.
    [Test]
    public void Decrypting_a_column_back_to_plaintext_is_refused_too()
    {
        var (generator, _, _) = Generate(new ColumnAlteration
        {
            OldColumn = Encrypted("body"),
            NewColumn = Plaintext("body"),
            Changes = { "Type", "EncryptionProfile" },
        });

        Assert.That(generator.BlockingIssues, Is.Not.Empty);
    }

    // A bytea column that was never encrypted is an ordinary byte[]; nothing special applies to it.
    [Test]
    public void An_ordinary_bytea_column_is_unaffected()
    {
        var (generator, up, _) = Generate(new ColumnAlteration
        {
            OldColumn = Plaintext("payload"),
            NewColumn = new DbColumn { Name = "payload", SourceName = "Payload", DatabaseType = "bytea", IsEncrypted = false },
            Changes = { "Type" },
        });

        Assert.Multiple(() =>
        {
            Assert.That(generator.BlockingIssues, Is.Empty);
            Assert.That(up, Does.Contain("::bytea"));
        });
    }

    // ── changing the profile ──

    [Test]
    public void Changing_the_encryption_profile_produces_a_visible_manual_marker_and_no_DDL()
    {
        var (generator, up, down) = Generate(new ColumnAlteration
        {
            OldColumn = Encrypted("body"),
            NewColumn = Encrypted("body", profile: "highsec"),
            Changes = { "EncryptionProfile" },
        });

        Assert.Multiple(() =>
        {
            Assert.That(generator.BlockingIssues, Is.Empty,
                "unlike encrypting in place, this is legitimate — it just cannot be done in SQL");
            Assert.That(up, Does.Contain(PostgreSqlGenerator.ManualMarker));
            Assert.That(up, Does.Contain("highsec"), "the message must name the profile being moved to");
            Assert.That(up, Does.Not.Contain("ALTER COLUMN"), "there is no DDL that could carry this out");
            Assert.That(down, Does.Contain(PostgreSqlGenerator.ManualMarker),
                "rolling back needs the same application-level pass in the other direction");
            Assert.That(generator.SafetyWarnings, Is.Not.Empty,
                "it must also be reported on the console, not only buried in the file");
        });
    }

    // ── the comparer half ──

    [Test]
    public void The_comparer_sees_a_profile_change_that_both_sides_render_as_bytea()
    {
        var diff = CompareOneColumn(Encrypted("body"), Encrypted("body", profile: "highsec"));

        Assert.That(diff.AlteredTables, Has.Count.EqualTo(1),
            "identical bytea on both sides is exactly why this used to be invisible");
        Assert.That(diff.AlteredTables[0].ModifiedColumns[0].Changes, Does.Contain("EncryptionProfile"));
    }

    [Test]
    public void An_unchanged_profile_is_not_a_change()
    {
        Assert.That(CompareOneColumn(Encrypted("body", "highsec"), Encrypted("body", "highsec")).AlteredTables,
            Is.Empty);
    }

    // Upgrading the tool must not invent a migration for every encrypted column in the schema: a snapshot
    // written before these fields existed records neither, and null means "not recorded", not "not encrypted".
    [Test]
    public void A_snapshot_predating_the_encryption_fields_does_not_diff()
    {
        var legacy = new DbColumn { Name = "body", SourceName = "Body", DatabaseType = "bytea" };
        // IsEncrypted and EncryptionProfile are both null, as they would be after JSON round-tripping an
        // old structure.json where the properties simply were not written.

        Assert.That(CompareOneColumn(legacy, Encrypted("body", "highsec")).AlteredTables, Is.Empty);
    }

    private static SchemaDiff CompareOneColumn(DbColumn oldCol, DbColumn newCol)
    {
        var id = new DbColumn { Name = "id", SourceName = "Id", DatabaseType = "uuid", IsPrimaryKey = true };
        var old = new DbSchema
        {
            Tables = [new DbTable { Name = "notes", SourceName = "App.Note", Columns = [id, oldCol] }],
        };
        var current = new DbSchema
        {
            Tables = [new DbTable { Name = "notes", SourceName = "App.Note", Columns = [id, newCol] }],
        };
        return SchemaComparer.Compare(old, current);
    }
}
