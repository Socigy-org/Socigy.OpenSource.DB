using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using static Socigy.OpenSource.DB.Tool.Tests.TestSchema;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// A column that is both RENAMED and MODIFIED (type/nullable/default change) in one diff. The UP renames first then
/// alters by the new name; the DOWN must revert the alteration (still by the new name) BEFORE renaming back, or the
/// alter targets a column the rename-back already renamed away ("column ... does not exist") and the rollback fails.
/// </summary>
[TestFixture]
public class RenameAndModifyColumnTests
{
    private static (List<string> Up, List<string> Down) GenRenameAndRetype()
    {
        // people.age (integer) -> people.age_years (bigint): a rename AND a type change on the same column.
        var table = Table("people", Col("id", "uuid", pk: true), Col("age_years", "bigint"));
        UseSchema(table);

        var alt = new TableAlteration { Table = table };
        alt.ProvideDefaults();
        alt.RenamedColumns.Add(new ColumnRename { Old = Col("age", "integer"), New = Col("age_years", "bigint") });
        alt.ModifiedColumns.Add(new ColumnAlteration
        {
            OldColumn = Col("age", "integer"),
            NewColumn = Col("age_years", "bigint"),
            Changes = { "Type" },
        });

        var (up, down) = new PostgreSqlGenerator().Generate(new SchemaDiff { AlteredTables = { alt } }, false);
        return (up.ToList(), down.ToList());
    }

    [Test]
    public void Up_renames_then_alters_by_new_name()
    {
        var (up, _) = GenRenameAndRetype();
        int rename = up.FindIndex(s => s.Contains("RENAME COLUMN \"age\" TO \"age_years\""));
        int retype = up.FindIndex(s => s.Contains("\"age_years\" TYPE bigint"));
        Assert.That(rename, Is.GreaterThanOrEqualTo(0), "UP must rename age -> age_years");
        Assert.That(retype, Is.GreaterThanOrEqualTo(0), "UP must alter the column by its new name");
        Assert.That(rename, Is.LessThan(retype), "UP must rename before altering by the new name");
    }

    [Test]
    public void Down_reverts_the_type_by_new_name_before_renaming_back()
    {
        var (_, down) = GenRenameAndRetype();

        int retypeRevert = down.FindIndex(s => s.Contains("\"age_years\" TYPE integer"));
        int renameBack = down.FindIndex(s => s.Contains("RENAME COLUMN \"age_years\" TO \"age\""));

        Assert.That(retypeRevert, Is.GreaterThanOrEqualTo(0),
            "DOWN must revert the type by the column's NEW name (the rename-back has not run yet)");
        Assert.That(renameBack, Is.GreaterThanOrEqualTo(0), "DOWN must rename age_years back to age");
        Assert.That(retypeRevert, Is.LessThan(renameBack),
            "DOWN must revert the type BEFORE renaming back, else it targets a column that no longer exists");

        // The revert must NOT reference the old name (the column is still called age_years at that point).
        Assert.That(down.Any(s => s.Contains("\"age\" TYPE integer")), Is.False,
            "the type revert must use the new name, not the old one");
    }
}
