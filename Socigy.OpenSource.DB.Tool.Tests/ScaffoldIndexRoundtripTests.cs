using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool.Scaffolding;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Scaffolding has to round-trip indexes: schema → C# classes → compile → schema. If the emitted attributes
/// do not read back the way they went in, the next <c>generate</c> against a scaffolded project produces a
/// migration that drops indexes the database already has.
/// </summary>
[TestFixture]
public class ScaffoldIndexRoundtripTests
{
    private static DbTable UsersWith(params DbIndex[] indexes) => new()
    {
        Name = "users",
        SourceName = "User",
        Columns =
        [
            new DbColumn { Name = "id", SourceName = "Id", DotnetType = "Guid", Nullable = false, IsPrimaryKey = true },
            new DbColumn { Name = "tenant_id", SourceName = "TenantId", DotnetType = "Guid", Nullable = false },
            new DbColumn { Name = "email", SourceName = "Email", DotnetType = "string", Nullable = false },
            new DbColumn { Name = "status", SourceName = "Status", DotnetType = "string", Nullable = true },
        ],
        Constraints = [],
        Indexes = indexes.ToList(),
    };

    private static DbIndex Index(params string[] properties) => new()
    {
        TableName = "users",
        Columns = properties,
    };

    private static string Emit(DbTable table) =>
        CSharpClassEmitter.Emit(new DbSchema { Tables = [table] }, "Fixture")["User.cs"];

    /// <summary>Emits the table, compiles the result, and reads the schema back out of the assembly.</summary>
    private static IList<DbIndex> RoundTrip(DbTable table)
    {
        var schema = AnalyzerModelCompiler.Analyze(Emit(table));
        return schema.Tables.First(t => t.Name == "users").Indexes ?? [];
    }

    [Test]
    public void Single_column_index_is_emitted_on_the_property()
    {
        Assert.That(Emit(UsersWith(Index("Email"))), Does.Contain("[Index]"));
    }

    [Test]
    public void Composite_index_is_emitted_on_the_class()
    {
        Assert.That(Emit(UsersWith(Index("TenantId", "Email"))),
            Does.Contain("[Index(nameof(TenantId), nameof(Email))]"));
    }

    // The reader always knows the database's actual index name, but stating it on every attribute is noise.
    // It only has to be carried when it differs from the name the generator would derive.
    [Test]
    public void A_derived_index_name_is_not_restated()
    {
        var derived = Index("Email");
        derived.Name = "IX_users_email";

        Assert.That(Emit(UsersWith(derived)), Does.Not.Contain("Name ="));
    }

    [Test]
    public void A_hand_written_index_name_is_preserved()
    {
        var custom = Index("Email");
        custom.Name = "email_lookup_v2";

        var roundTripped = RoundTrip(UsersWith(custom));

        Assert.That(roundTripped.Single().Name, Is.EqualTo("email_lookup_v2"),
            "a name the tool would not derive has to survive, or the next migration recreates the index");
    }

    [Test]
    public void Several_indexes_on_one_column_all_survive()
    {
        var plain = Index("Email");
        var partial = Index("Email");
        partial.Where = "status <> 'deleted'";

        Assert.That(RoundTrip(UsersWith(plain, partial)), Has.Count.EqualTo(2));
    }

    [Test]
    public void Every_option_survives_the_round_trip()
    {
        var index = Index("TenantId", "Email");
        index.IsUnique = true;
        index.Method = DbIndexMethods.Hash;
        index.Where = "status <> 'deleted'";
        index.IncludeColumns = ["Status"];
        index.DescendingColumns = ["Email"];
        index.NullsLastColumns = ["Email"];

        var result = RoundTrip(UsersWith(index)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(result.Columns, Is.EqualTo(new[] { "TenantId", "Email" }));
            Assert.That(result.IsUnique, Is.True);
            Assert.That(result.Method, Is.EqualTo(DbIndexMethods.Hash));
            Assert.That(result.Where, Is.EqualTo("status <> 'deleted'"));
            Assert.That(result.IncludeColumns, Is.EqualTo(new[] { "Status" }));
            Assert.That(result.DescendingColumns, Is.EqualTo(new[] { "Email" }));
            Assert.That(result.NullsLastColumns, Is.EqualTo(new[] { "Email" }));
        });
    }

    [Test]
    public void An_engine_specific_access_method_survives_the_round_trip()
    {
        var index = Index("Email");
        index.RawMethod = "spgist";

        Assert.That(RoundTrip(UsersWith(index)).Single().RawMethod, Is.EqualTo("spgist"));
    }

    // The whole point: a scaffolded model diffed against the database it came from must be a no-op.
    [Test]
    public void A_scaffolded_model_generates_no_index_migration_against_its_own_database()
    {
        var index = Index("TenantId", "Email");
        index.IsUnique = true;
        index.Where = "status <> 'deleted'";
        var fromDatabase = UsersWith(index);

        var fromScaffoldedCode = AnalyzerModelCompiler.Analyze(Emit(fromDatabase))
            .Tables.First(t => t.Name == "users");

        var diff = SchemaComparer.Compare(
            new DbSchema { Id = "db", Tables = [fromDatabase] },
            new DbSchema { Id = "code", Tables = [fromScaffoldedCode] });

        var alteration = diff.AlteredTables.FirstOrDefault();
        Assert.That(alteration?.AddedIndexes ?? [], Is.Empty, "scaffolding must not invent an index");
        Assert.That(alteration?.RemovedIndexes ?? [], Is.Empty, "nor propose dropping one that already exists");
    }
}
