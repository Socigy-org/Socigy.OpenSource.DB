using System.Collections.Generic;
using NUnit.Framework;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool.Introspection;
using Socigy.OpenSource.DB.Tool.Scaffolding;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// The schema READER stores constraint columns as PascalCase (== the property SourceName) and populates FK
/// referential actions; the EMITTER must honor both, or a scaffold -> generate round-trip silently drops a UNIQUE
/// constraint or a CASCADE / SET NULL action. Also, a non-identifier DB name must be sanitized to compilable C#.
/// These feed the emitter the casing/shape the real reader produces (the prior tests fed DB-case input).
/// </summary>
[TestFixture]
public class ScaffoldRoundtripFidelityTests
{
    [Test]
    public void SingleColumnUnique_WithPascalCaseConstraintColumn_EmitsUnique()
    {
        var t = new DbTable
        {
            Name = "accounts",
            SourceName = "Account",
            Columns = new List<DbColumn>
            {
                new DbColumn { Name = "id", SourceName = "Id", DotnetType = "Guid", Nullable = false, IsPrimaryKey = true },
                new DbColumn { Name = "email", SourceName = "Email", DotnetType = "string", Nullable = false },
            },
            // The reader emits PascalCase constraint columns (Naming.ToPascalCase), NOT the DB snake_case name.
            Constraints = new List<DbConstraint>
            {
                new DbConstraint { Type = DbConstraint.Types.Unique, TableName = "accounts", Columns = new[] { "Email" } },
            }
        };

        var src = CSharpClassEmitter.Emit(new DbSchema { Tables = new List<DbTable> { t } }, "MyApp.Data")["Account.cs"];
        Assert.That(src, Does.Match(@"\[Unique\]\s*\r?\n\s*public string Email"),
            "a single-column UNIQUE whose constraint column is PascalCase (as the reader produces) must still emit [Unique]");
    }

    [Test]
    public void ForeignKey_ReferentialActions_AreEmitted()
    {
        var orders = new DbTable
        {
            Name = "orders",
            SourceName = "Order",
            Columns = new List<DbColumn>
            {
                new DbColumn { Name = "id", SourceName = "Id", DotnetType = "Guid", Nullable = false, IsPrimaryKey = true },
                new DbColumn { Name = "user_id", SourceName = "UserId", DotnetType = "Guid", Nullable = false },
            },
            Constraints = new List<DbConstraint>
            {
                new DbConstraint
                {
                    Type = DbConstraint.Types.ForeignKey,
                    TableName = "orders",
                    Columns = new[] { "UserId" },
                    TargetTable = "User",
                    TargetColumns = new[] { "Id" },
                    OnDelete = DbValues.ForeignKey.Cascade,
                    OnUpdate = DbValues.ForeignKey.Restrict,
                }
            }
        };

        var src = CSharpClassEmitter.Emit(new DbSchema { Tables = new List<DbTable> { orders } }, "MyApp.Data")["Order.cs"];
        Assert.That(src, Does.Contain("OnDelete = DbValues.ForeignKey.Cascade"),
            "a CASCADE on delete must round-trip, not be silently dropped");
        Assert.That(src, Does.Contain("OnUpdate = DbValues.ForeignKey.Restrict"));
    }

    [TestCase("user_id", "UserId")]          // normal case unchanged
    [TestCase("2fa_enabled", "_2faEnabled")] // digit-leading -> underscore-prefixed (C# forbids a digit start)
    [TestCase("weird.name", "WeirdName")]    // punctuation split out, not leaked into the identifier
    public void ToPascalCase_ProducesValidIdentifier(string dbName, string expected)
    {
        Assert.That(Naming.ToPascalCase(dbName), Is.EqualTo(expected));
    }
}
