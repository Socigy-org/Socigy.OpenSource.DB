using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Socigy.OpenSource.DB.Tool.Migrations;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// A multi-table migration must name all affected tables, not just the first (issue #5). The generated name is
/// <c>{id}_{prefix}_{hash}</c>, so we assert on the prefix segment.
/// </summary>
[TestFixture]
public class MigrationNamerTests
{
    private static SchemaDiff Added(params string[] names)
        => new SchemaDiff { AddedTables = names.Select(n => new DbTable { Name = n }).ToList() };

    [Test]
    public void Single_added_table_names_it()
        => Assert.That(MigrationNamer.GenerateUniqueName(Added("users")), Does.Contain("_AddUsers_"));

    [Test]
    public void Two_added_tables_name_both()
        => Assert.That(MigrationNamer.GenerateUniqueName(Added("users", "outbox")), Does.Contain("_AddUsersAndOutbox_"));

    [Test]
    public void More_than_two_added_tables_are_summarized()
        => Assert.That(MigrationNamer.GenerateUniqueName(Added("users", "outbox", "events")), Does.Contain("_AddUsersAnd2More_"));

    [Test]
    public void Removed_tables_use_the_remove_prefix()
    {
        var diff = new SchemaDiff { RemovedTables = new List<DbTable> { new() { Name = "users" }, new() { Name = "outbox" } } };
        Assert.That(MigrationNamer.GenerateUniqueName(diff), Does.Contain("_RemoveUsersAndOutbox_"));
    }
}
