using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Socigy.OpenSource.DB.Tool.Migrations;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Detecting a forked migration chain at GENERATE time.
///
/// A new migration's <c>PreviousId</c> comes from the schema snapshot, and the snapshot only advances after
/// the file is written — so two attempts at the same change both claim the same parent, and the fresh
/// timestamp in the file name means the tool cannot overwrite its own prior emission either. The fork was
/// only ever caught at apply time, where it aborts inside whatever fixture applies the schema and names
/// neither file as the cause — so one leftover file from an attempt nobody kept can take an entire module's
/// schema offline, and with it every test that touches the database.
/// </summary>
[TestFixture]
public class MigrationChainInspectorTests
{
    private string _folder = "";

    [SetUp]
    public void CreateFolder()
    {
        _folder = Path.Combine(Path.GetTempPath(), "socigy-chain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void RemoveFolder()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>Writes a file shaped like the T4 template's output — only the two constants matter.</summary>
    private void WriteMigration(string id, string previousId)
    {
        string previous = previousId == null
            ? "#nullable enable\n        public string? PreviousId => null;\n#nullable disable"
            : $"public const string _PreviousId = \"{previousId}\";\n        public string PreviousId => _PreviousId;";

        File.WriteAllText(Path.Combine(_folder, id + ".g.cs"), $@"
namespace Fixture.Socigy.Migrations
{{
    public class M_{id} : ILocalMigration
    {{
        public const string _Id = ""{id}"";
        {previous}
        public const string _UpSql = """""" SELECT 1; """""";
        public const string _DownSql = """""" SELECT 1; """""";
    }}
}}");
    }

    [Test]
    public void An_empty_folder_has_nothing_to_conflict_with()
    {
        Assert.That(MigrationChainInspector.DetectConflicts(MigrationChainInspector.Read(_folder), null), Is.Empty);
    }

    [Test]
    public void Reading_recovers_the_id_and_parent_of_every_migration()
    {
        WriteMigration("20260731120000_Initial", null);
        WriteMigration("20260731130000_AddUsers", "20260731120000_Initial");

        var read = MigrationChainInspector.Read(_folder);

        Assert.Multiple(() =>
        {
            Assert.That(read, Has.Count.EqualTo(2));
            Assert.That(read.Single(m => m.Id == "20260731120000_Initial").PreviousId, Is.Null,
                "the root declares PreviousId => null rather than a constant");
            Assert.That(read.Single(m => m.Id == "20260731130000_AddUsers").PreviousId,
                Is.EqualTo("20260731120000_Initial"));
        });
    }

    [Test]
    public void Appending_to_the_tip_of_a_healthy_chain_is_allowed()
    {
        WriteMigration("20260731120000_Initial", null);
        WriteMigration("20260731130000_AddUsers", "20260731120000_Initial");

        var issues = MigrationChainInspector.DetectConflicts(
            MigrationChainInspector.Read(_folder), "20260731130000_AddUsers");

        Assert.That(issues, Is.Empty);
    }

    // The common case: an abandoned regeneration is left in the folder, then the same change is generated
    // again against the same (un-advanced) snapshot.
    [Test]
    public void A_sibling_already_claiming_the_parent_blocks_generation_and_is_named()
    {
        WriteMigration("20260731120000_Initial", null);
        WriteMigration("20260731155951_AddMakeUpObligations", "20260731120000_Initial");   // abandoned

        var issues = MigrationChainInspector.DetectConflicts(
            MigrationChainInspector.Read(_folder), "20260731120000_Initial");

        Assert.That(issues, Is.Not.Empty);
        Assert.That(string.Join("\n", issues), Does.Contain("20260731155951_AddMakeUpObligations.g.cs"),
            "the diagnosis, not the deletion, is what costs the time here — so name the file");
    }

    // Root migrations fork too: PreviousId is null on both sides.
    [Test]
    public void A_second_root_migration_is_blocked()
    {
        WriteMigration("20260731120000_Initial", null);

        var issues = MigrationChainInspector.DetectConflicts(MigrationChainInspector.Read(_folder), null);

        Assert.That(issues, Is.Not.Empty);
    }

    // A fork that already exists on disk is reported even when the new migration would attach elsewhere,
    // because appending to a chain that cannot be applied is not progress.
    [Test]
    public void An_existing_fork_is_reported_even_when_the_new_migration_attaches_to_the_tip()
    {
        WriteMigration("20260731120000_Initial", null);
        WriteMigration("20260731155951_AddA", "20260731120000_Initial");
        WriteMigration("20260731160059_AddB", "20260731120000_Initial");   // already forked

        var issues = MigrationChainInspector.DetectConflicts(
            MigrationChainInspector.Read(_folder), "20260731160059_AddB");

        Assert.That(string.Join("\n", issues), Does.Contain("single chain"));
    }

    [Test]
    public void A_file_that_is_not_a_migration_is_ignored()
    {
        WriteMigration("20260731120000_Initial", null);
        File.WriteAllText(Path.Combine(_folder, "Helpers.g.cs"), "namespace X { public class Y { } }");

        Assert.That(MigrationChainInspector.Read(_folder), Has.Count.EqualTo(1));
    }
}
