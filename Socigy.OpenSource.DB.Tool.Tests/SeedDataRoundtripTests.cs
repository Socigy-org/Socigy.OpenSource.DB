using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Socigy.OpenSource.DB.Tool;
using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using static Socigy.OpenSource.DB.Tool.Tests.TestSchema;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Seed rows (enum-table <c>InstantiatedValues</c>) are persisted to structure.json and reloaded as the saved
/// schema. A <c>null</c> seed value (a member without a [Description]) reloads boxed as a JsonElement(Null), while
/// the freshly-analyzed side holds a CLR null. If the comparer treats those as different, every regeneration emits
/// a no-op "row modified" data migration. They must compare equal.
/// </summary>
[TestFixture]
public class SeedDataRoundtripTests
{
    private static DbTable MakeSeededTable()
    {
        var t = Table("statuses",
            Col("id", "integer", pk: true),
            Col("value", "text"),
            Col("description", "text", nullable: true));
        t.InstantiatedValues = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1, ["value"] = "Active", ["description"] = null },
            new() { ["id"] = 2, ["value"] = "Closed", ["description"] = "is closed" },
        };
        return t;
    }

    [Test]
    public void NullSeedValue_SurvivesJsonRoundTrip_NoSpuriousDataChange()
    {
        var current = new DbSchema { Tables = { MakeSeededTable() } };

        // Simulate the structure.json persistence: serialize + deserialize so the null seed value comes back as a
        // JsonElement(Null) (exactly how SavedSchema is loaded), then compare against the fresh CLR-valued side.
        string json = JsonSerializer.Serialize(new DbSchema { Tables = { MakeSeededTable() } }, Configuration.JsonOptions);
        var savedReloaded = JsonSerializer.Deserialize<DbSchema>(json, Configuration.JsonOptions)!;

        var diff = SchemaComparer.Compare(savedReloaded, current);

        var modifiedRows = diff.AlteredTables.SelectMany(a => a.ModifiedRows ?? new List<RowAlteration>()).ToList();
        Assert.That(modifiedRows, Is.Empty,
            "an unchanged seed row with a null column must not be reported as modified after a JSON round-trip");
        Assert.That(diff.IsEmpty, Is.True, "identical schema + seed data must produce no migration");
    }
}
