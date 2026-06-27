using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using static Socigy.OpenSource.DB.Tool.Tests.TestSchema;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// A migration's DOWN script must exactly invert its UP. These cover three rollback bugs: a dropped table's
/// foreign keys must be re-created on rollback; a constraint dropped together with its column must be re-added
/// AFTER the column on rollback; and a JSON-round-tripped seed string must keep its quotes.
/// </summary>
[TestFixture]
public class RollbackInversionTests
{
    private static (List<string> Up, List<string> Down) Generate(SchemaDiff diff)
    {
        var (up, down) = new PostgreSqlGenerator().Generate(diff, isFirstMigration: false);
        return (up.ToList(), down.ToList());
    }

    [Test]
    public void RemovedTable_Down_ReCreatesForeignKeys()
    {
        var users = Table("users", Col("id", "uuid", pk: true));
        var orders = Table("orders", Col("id", "uuid", pk: true), Col("user_id", "uuid"));
        orders.Constraints!.Add(ForeignKey("orders", "user_id", "users", "id"));
        UseSchema(users, orders);

        var (_, down) = Generate(new SchemaDiff { RemovedTables = { orders } });

        int createIdx = down.FindIndex(s => s.Contains("CREATE TABLE") && s.Contains("\"orders\""));
        int fkIdx = down.FindIndex(s => s.Contains("ADD CONSTRAINT") && s.Contains("FOREIGN KEY"));
        Assert.That(createIdx, Is.GreaterThanOrEqualTo(0), "DOWN must re-create the dropped table");
        Assert.That(fkIdx, Is.GreaterThan(createIdx), "DOWN must re-add the FK after the table is re-created");
    }

    [Test]
    public void DroppedConstraintAndColumn_Down_ReAddsColumnBeforeConstraint()
    {
        var users = Table("users", Col("id", "uuid", pk: true));
        var alt = new TableAlteration { Table = users };
        alt.ProvideDefaults();
        alt.RemovedColumns.Add(Col("email", "text"));
        alt.RemovedConstraints.Add(new DbConstraint
        {
            Type = DbConstraint.Types.Unique, TableName = "users", Name = "UQ_users_email", Columns = new[] { "email" }
        });
        UseSchema(users);

        var (_, down) = Generate(new SchemaDiff { AlteredTables = { alt } });

        int colIdx = down.FindIndex(s => s.Contains("ADD COLUMN") && s.Contains("\"email\""));
        int conIdx = down.FindIndex(s => s.Contains("ADD CONSTRAINT") && s.Contains("UNIQUE"));
        Assert.That(colIdx, Is.GreaterThanOrEqualTo(0), "DOWN must re-add the dropped column");
        Assert.That(conIdx, Is.GreaterThan(colIdx),
            "DOWN must re-add the unique constraint AFTER its column exists, else apply fails");
    }

    [Test]
    public void Seed_NumericLookingString_KeepsQuotes_OnRestore()
    {
        var lookups = Table("lookups", Col("id", "integer", pk: true), Col("description", "text"));
        // Mimic the JSON-deserialized saved schema: values come back as JsonElement, not the original CLR type.
        var row = new Dictionary<string, object?>
        {
            ["id"] = JsonDocument.Parse("404").RootElement.Clone(),
            ["description"] = JsonDocument.Parse("\"404\"").RootElement.Clone(),
        };
        lookups.InstantiatedValues = new List<Dictionary<string, object?>> { row };
        UseSchema(lookups);

        var (_, down) = Generate(new SchemaDiff { RemovedTables = { lookups } });

        var insert = down.First(s => s.Contains("INSERT INTO") && s.Contains("\"lookups\""));
        // description (a text column) must be the quoted string '404', not the bare integer 404.
        Assert.That(insert, Does.Contain("'404'"), "a numeric-looking string seed must stay quoted");
        // id (a JSON number) must be the bare integer.
        Assert.That(insert, Does.Match(@"VALUES \(404, '404'\)"));
    }
}
