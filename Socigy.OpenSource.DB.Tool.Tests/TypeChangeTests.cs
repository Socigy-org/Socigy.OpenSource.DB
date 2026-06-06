using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using static Socigy.OpenSource.DB.Tool.Tests.TestSchema;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Column type changes are emitted as a blind <c>USING col::newtype</c> cast, which can fail or lose
/// precision when narrowing. A narrowing cast must be flagged for review; a known-safe widening must not be.
/// </summary>
[TestFixture]
public class TypeChangeTests
{
    private const string Marker = PostgreSqlGenerator.LossyMarker;

    private static (List<string> Up, List<string> Down) GenTypeChange(string oldDb, string newDb)
    {
        var table = Table("orders", Col("id", "uuid", pk: true), Col("amount", newDb));
        UseSchema(table);

        var alt = new TableAlteration { Table = table };
        alt.ProvideDefaults();
        alt.ModifiedColumns.Add(new ColumnAlteration
        {
            OldColumn = Col("amount", oldDb),
            NewColumn = Col("amount", newDb),
            Changes = { "Type" },
        });

        var (up, down) = new PostgreSqlGenerator().Generate(new SchemaDiff { AlteredTables = { alt } }, false);
        return (up.ToList(), down.ToList());
    }

    private static bool MarkerBefore(List<string> sql, string contains)
    {
        int i = sql.FindIndex(s => s.Contains(contains) && !s.StartsWith("--"));
        return i > 0 && sql[i - 1].Contains(Marker);
    }

    [Test]
    public void Narrowing_type_change_is_flagged_lossy()
    {
        var (up, _) = GenTypeChange("bigint", "smallint");
        Assert.That(MarkerBefore(up, "TYPE smallint"), Is.True,
            "narrowing bigint -> smallint must be flagged as a lossy cast");
    }

    [Test]
    public void Widening_type_change_is_not_flagged()
    {
        var (up, _) = GenTypeChange("smallint", "bigint");
        Assert.That(MarkerBefore(up, "TYPE bigint"), Is.False,
            "widening smallint -> bigint is safe and must not be flagged");
    }

    [Test]
    public void Widening_up_still_flags_the_narrowing_down()
    {
        // UP widens (safe), but DOWN reverses to the narrower type (lossy) and must be flagged.
        var (_, down) = GenTypeChange("smallint", "bigint");
        Assert.That(MarkerBefore(down, "TYPE smallint"), Is.True,
            "the DOWN script narrows back and must be flagged");
    }

    [Test]
    public void Unrelated_type_change_flags_both_directions()
    {
        // uuid <-> integer is not castable as a safe widening in either direction.
        var (up, down) = GenTypeChange("uuid", "integer");
        Assert.That(MarkerBefore(up, "TYPE integer"), Is.True);
        Assert.That(MarkerBefore(down, "TYPE uuid"), Is.True);
    }

    [Test]
    public void Conversion_to_text_is_always_safe()
    {
        // Every value has a text representation, so ::text never fails or loses data.
        var (up, _) = GenTypeChange("integer", "text");
        Assert.That(MarkerBefore(up, "TYPE text"), Is.False);
    }
}
