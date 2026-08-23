using System.Linq;
using NUnit.Framework;
using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Which name <c>[Renamed]</c> takes.
///
/// A column has two names — the C# property name and the snake_case database name — and for a long time the
/// comparer matched <c>[Renamed]</c> against the C# one while the generator's own remedy message printed the
/// database one. Following the tool's advice verbatim therefore did nothing: the attribute was read, never
/// matched, and the column fell through to a destructive DROP + ADD. Class-level <c>[Renamed]</c> meanwhile
/// matched the database *table* name, the opposite convention from columns, with nothing saying so.
///
/// The contract these pin down: **either name is accepted, at both levels**, and the advice the tool prints is
/// a name that actually matches.
/// </summary>
[TestFixture]
public class RenamedAttributeMatchingTests
{
    private static DbSchema Schema(params DbTable[] tables) => new() { Tables = tables.ToList() };

    private static DbColumn Column(string dbName, string sourceName, string type = "uuid", string renamedFrom = null)
        => new()
        {
            Name = dbName,
            SourceName = sourceName,
            DatabaseType = type,
            RenamedFrom = renamedFrom,
        };

    private static DbTable Table(string dbName, string sourceName, params DbColumn[] columns)
        => new() { Name = dbName, SourceName = sourceName, Columns = columns.ToList() };

    // The classic shape: [Column("owner_id")] Guid OwnerId  ->
    // [Renamed(?)] [Column("author_id")] Guid AuthorId, where the two names differ in both spaces.
    private static SchemaDiff CompareWithRenamedFrom(string renamedFrom)
    {
        var old = Schema(Table("posts", "App.Post",
            Column("id", "Id"),
            Column("owner_id", "OwnerId")));

        var current = Schema(Table("posts", "App.Post",
            Column("id", "Id"),
            Column("author_id", "AuthorId", renamedFrom: renamedFrom)));

        return SchemaComparer.Compare(old, current);
    }

    [TestCase("OwnerId", TestName = "Renamed_accepts_the_CSharp_property_name")]
    [TestCase("owner_id", TestName = "Renamed_accepts_the_database_column_name")]
    public void Renamed_on_a_column_matches_under_either_name(string renamedFrom)
    {
        var diff = CompareWithRenamedFrom(renamedFrom);

        Assert.That(diff.AlteredTables, Has.Count.EqualTo(1));
        var alteration = diff.AlteredTables[0];

        Assert.Multiple(() =>
        {
            Assert.That(alteration.RenamedColumns, Has.Count.EqualTo(1),
                "a rename preserves the data; a DROP + ADD destroys it");
            Assert.That(alteration.AddedColumns, Is.Empty);
            Assert.That(alteration.RemovedColumns, Is.Empty);
            Assert.That(alteration.RenamedColumns[0].Old.Name, Is.EqualTo("owner_id"));
            Assert.That(alteration.RenamedColumns[0].New.Name, Is.EqualTo("author_id"));
        });
    }

    // The message is the whole point of this issue: the one value the tool told you to write was the one
    // value that could never match. Whatever it prints must be a name the comparer accepts.
    [Test]
    public void The_unmarked_rename_warning_prints_a_name_that_actually_matches()
    {
        var old = Schema(Table("posts", "App.Post",
            Column("id", "Id"),
            Column("owner_id", "OwnerId")));

        var current = Schema(Table("posts", "App.Post",
            Column("id", "Id"),
            Column("author_id", "AuthorId")));   // no [Renamed] — this is what triggers the advice

        var diff = SchemaComparer.Compare(old, current);
        var generator = new PostgreSqlGenerator();
        generator.Generate(diff, isFirstMigration: false);

        var advice = generator.SafetyWarnings.FirstOrDefault(w => w.Contains("[Renamed("));
        Assert.That(advice, Is.Not.Null, "an unmarked rename must still be flagged");

        // Extract the argument the message tells the user to write, then prove the comparer honours it.
        int start = advice.IndexOf("[Renamed(\"", System.StringComparison.Ordinal) + "[Renamed(\"".Length;
        int end = advice.IndexOf('"', start);
        string suggested = advice.Substring(start, end - start);

        Assert.That(CompareWithRenamedFrom(suggested).AlteredTables[0].RenamedColumns, Has.Count.EqualTo(1),
            $"the tool suggested [Renamed(\"{suggested}\")], which must be a name the comparer matches");
    }

    [TestCase("App.Comment", TestName = "Class_Renamed_accepts_the_CSharp_class_name")]
    [TestCase("articles", TestName = "Class_Renamed_accepts_the_database_table_name")]
    public void Renamed_on_a_class_matches_under_either_name(string renamedFrom)
    {
        var old = Schema(Table("articles", "App.Comment", Column("id", "Id")));

        var current = Schema(new DbTable
        {
            Name = "comments",
            SourceName = "App.Comment",
            RenamedFrom = renamedFrom,
            Columns = [Column("id", "Id")],
        });

        var diff = SchemaComparer.Compare(old, current);

        Assert.Multiple(() =>
        {
            Assert.That(diff.RenamedTables, Has.Count.EqualTo(1));
            Assert.That(diff.AddedTables, Is.Empty);
            Assert.That(diff.RemovedTables, Is.Empty);
        });
    }
}
