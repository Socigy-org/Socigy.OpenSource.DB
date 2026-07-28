using System.Linq;
using NUnit.Framework;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using static Socigy.OpenSource.DB.Tool.Tests.TestSchema;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Diffing indexes between the saved snapshot and the current model. Two properties matter: an unchanged
/// model must produce no migration at all (otherwise every regeneration churns out a drop-and-recreate of
/// every index), and any real difference must surface, because an index cannot be altered in place.
/// </summary>
[TestFixture]
public class IndexComparisonTests
{
    private static DbTable Users(params DbIndex[] indexes)
    {
        var table = Table("users", Col("id", "uuid", pk: true), Col("email", "text"), Col("status", "text"));
        table.Indexes = indexes.ToList();
        return table;
    }

    private static DbIndex Index(params string[] columns) => new()
    {
        TableName = "users",
        Columns = columns,
    };

    private static TableAlteration Diff(DbTable oldTable, DbTable newTable)
    {
        var diff = SchemaComparer.Compare(
            new DbSchema { Id = "old", Tables = [oldTable] },
            new DbSchema { Id = "new", Tables = [newTable] });
        return diff.AlteredTables.FirstOrDefault();
    }

    [Test]
    public void An_unchanged_index_produces_no_migration()
    {
        Assert.That(Diff(Users(Index("email")), Users(Index("email"))), Is.Null,
            "regenerating an unchanged model must not emit a drop-and-recreate");
    }

    [Test]
    public void An_index_only_change_still_produces_a_migration()
    {
        var alteration = Diff(Users(), Users(Index("email")));

        Assert.That(alteration, Is.Not.Null, "an added index is a schema change on its own");
        Assert.That(alteration.AddedIndexes, Has.Count.EqualTo(1));
    }

    [Test]
    public void A_dropped_index_is_reported_as_removed()
    {
        var alteration = Diff(Users(Index("email")), Users());

        Assert.That(alteration, Is.Not.Null);
        Assert.That(alteration.RemovedIndexes, Has.Count.EqualTo(1));
    }

    [Test]
    public void A_snapshot_predating_index_support_reads_as_having_none()
    {
        // Indexes were added to DbTable after the fact, so an older snapshot deserializes them as null.
        var old = Users();
        old.Indexes = null;

        var alteration = Diff(old, Users(Index("email")));

        Assert.Multiple(() =>
        {
            Assert.That(alteration.AddedIndexes, Has.Count.EqualTo(1));
            Assert.That(alteration.RemovedIndexes, Is.Empty);
        });
    }

    // Every option is part of the index's identity: changing one is a drop plus a create, never a no-op.
    [Test]
    public void Changing_uniqueness_redefines_the_index()
    {
        var unique = Index("email");
        unique.IsUnique = true;

        var alteration = Diff(Users(Index("email")), Users(unique));

        Assert.Multiple(() =>
        {
            Assert.That(alteration.AddedIndexes, Has.Count.EqualTo(1));
            Assert.That(alteration.RemovedIndexes, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Changing_key_column_order_redefines_the_index()
    {
        var alteration = Diff(Users(Index("email", "status")), Users(Index("status", "email")));

        Assert.That(alteration, Is.Not.Null,
            "an index on (a, b) does not serve the same queries as one on (b, a)");
    }

    [Test]
    public void Changing_the_filter_redefines_the_index()
    {
        var filtered = Index("email");
        filtered.Where = "status <> 'deleted'";

        Assert.That(Diff(Users(Index("email")), Users(filtered)), Is.Not.Null);
    }

    [Test]
    public void Changing_the_method_redefines_the_index()
    {
        var hashed = Index("email");
        hashed.Method = DbIndexMethods.Hash;

        Assert.That(Diff(Users(Index("email")), Users(hashed)), Is.Not.Null);
    }

    [Test]
    public void Changing_covering_columns_redefines_the_index()
    {
        var covering = Index("email");
        covering.IncludeColumns = ["status"];

        Assert.That(Diff(Users(Index("email")), Users(covering)), Is.Not.Null);
    }

    [Test]
    public void Changing_ordering_redefines_the_index()
    {
        var descending = Index("email");
        descending.DescendingColumns = ["email"];

        Assert.That(Diff(Users(Index("email")), Users(descending)), Is.Not.Null);
    }

    // Option sets are unordered, so a reordering of the same values is not a change.
    [Test]
    public void Reordering_covering_columns_is_not_a_change()
    {
        var a = Index("email");
        a.IncludeColumns = ["status", "id"];
        var b = Index("email");
        b.IncludeColumns = ["id", "status"];

        Assert.That(Diff(Users(a), Users(b)), Is.Null);
    }

    [Test]
    public void Renaming_an_index_redefines_it()
    {
        var named = Index("email");
        named.Name = "ix_email";

        Assert.That(Diff(Users(Index("email")), Users(named)), Is.Not.Null);
    }
}
