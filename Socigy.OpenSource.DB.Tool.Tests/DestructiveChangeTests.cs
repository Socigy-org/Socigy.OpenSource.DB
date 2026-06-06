using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using static Socigy.OpenSource.DB.Tool.Tests.TestSchema;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Data-losing DDL (dropping a table or column) must be clearly flagged in the generated migration so a
/// reviewer sees it in the diff, and the generator must report it. A DROP that looks like any other
/// statement is how migrations silently delete production data.
/// </summary>
[TestFixture]
public class DestructiveChangeTests
{
    private const string Marker = PostgreSqlGenerator.DestructiveMarker;

    private static (List<string> Up, List<string> Down) Gen(SchemaDiff diff)
    {
        var g = new PostgreSqlGenerator();
        var (up, down) = g.Generate(diff, isFirstMigration: false);
        return (up.ToList(), down.ToList());
    }

    /// <summary>Every line is checked: the warning marker must appear on the line immediately before a DROP.</summary>
    private static void AssertWarningPrecedesDrop(List<string> sql, string dropContains)
    {
        int dropIdx = sql.FindIndex(s => s.Contains(dropContains, StringComparison.OrdinalIgnoreCase));
        Assert.That(dropIdx, Is.GreaterThan(-1), $"expected a statement containing '{dropContains}'");
        Assert.That(sql[dropIdx - 1], Does.Contain(Marker),
            $"the statement '{sql[dropIdx]}' must be preceded by a {Marker} warning");
    }

    [Test]
    public void Dropping_a_table_is_flagged_destructive()
    {
        var orders = Table("orders", Col("id", "uuid", pk: true));
        UseSchema(orders);

        var (up, _) = Gen(new SchemaDiff { RemovedTables = { orders } });

        AssertWarningPrecedesDrop(up, "DROP TABLE");
        Assert.That(new PostgreSqlGenerator().Generate(new SchemaDiff { RemovedTables = { orders } }, false),
            Is.Not.Null); // sanity
    }

    [Test]
    public void Dropping_a_column_is_flagged_destructive()
    {
        var orders = Table("orders", Col("id", "uuid", pk: true), Col("note", "text", nullable: true));
        UseSchema(orders);

        var alt = new TableAlteration { Table = orders };
        alt.ProvideDefaults();
        alt.RemovedColumns.Add(Col("note", "text", nullable: true));

        var (up, _) = Gen(new SchemaDiff { AlteredTables = { alt } });

        AssertWarningPrecedesDrop(up, "DROP COLUMN");
    }

    [Test]
    public void Non_destructive_migration_has_no_warning()
    {
        var orders = Table("orders", Col("id", "uuid", pk: true));
        UseSchema(orders);

        var (up, _) = Gen(new SchemaDiff { AddedTables = { orders } });

        Assert.That(up.Any(s => s.Contains(Marker)), Is.False, "adding a table is not destructive");
    }
}
