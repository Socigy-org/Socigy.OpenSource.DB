using System.Collections.Generic;
using NUnit.Framework;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool.Scaffolding;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// The emitter must produce attributes that <c>AssemblyAnalyzer</c> reads back identically, so
/// schema → classes → schema is stable. These assert the attribute surface for representative columns.
/// </summary>
[TestFixture]
public class CSharpClassEmitterTests
{
    private static string EmitUsers()
    {
        var users = new DbTable
        {
            Name = "users",
            SourceName = "User",
            Columns = new List<DbColumn>
            {
                new DbColumn { Name = "id", SourceName = "Id", DotnetType = "Guid", Nullable = false, IsPrimaryKey = true, DefaultValue = DbDefaults.Guid.Random },
                new DbColumn { Name = "seq", SourceName = "Seq", DotnetType = "int", Nullable = false, IsAutoIncrement = true },
                new DbColumn { Name = "username", SourceName = "Username", DotnetType = "string", Nullable = false, MaxLength = 50 },
                new DbColumn { Name = "data", SourceName = "Data", DotnetType = "string", Nullable = true, IsJsonColumn = true },
                new DbColumn { Name = "legacy_code", SourceName = "Code", DotnetType = "int", Nullable = true },
            },
            Constraints = new List<DbConstraint>()
        };

        var schema = new DbSchema { Tables = new List<DbTable> { users } };
        return CSharpClassEmitter.Emit(schema, "MyApp.Data")["User.cs"];
    }

    [Test]
    public void Emits_TableAndClass()
    {
        var src = EmitUsers();
        Assert.That(src, Does.Contain("namespace MyApp.Data;"));
        Assert.That(src, Does.Contain("[Table(\"users\")]"));
        Assert.That(src, Does.Contain("public partial class User"));
    }

    [Test]
    public void Emits_PrimaryKeyAndDefaultToken()
    {
        var src = EmitUsers();
        Assert.That(src, Does.Contain("PrimaryKey"));
        Assert.That(src, Does.Contain("Default(\"$socigy$guid.random\")"));
        Assert.That(src, Does.Contain("public Guid Id"));
    }

    [Test]
    public void Emits_AutoIncrement_StringLength_RawJson()
    {
        var src = EmitUsers();
        Assert.That(src, Does.Contain("AutoIncrement"));
        Assert.That(src, Does.Contain("StringLength(50)"));
        Assert.That(src, Does.Contain("RawJsonColumn"));
        Assert.That(src, Does.Contain("public string? Data"));
    }

    [Test]
    public void Emits_ColumnAttribute_OnlyWhenNameDiffers()
    {
        var src = EmitUsers();
        // 'username' snake_cases from 'Username' → no [Column]; 'legacy_code' differs from 'Code' → [Column].
        Assert.That(src, Does.Contain("Column(\"legacy_code\")"));
        Assert.That(src, Does.Not.Contain("Column(\"username\")"));
    }

    [Test]
    public void Emits_ForeignKeyAttribute()
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
                }
            }
        };

        var src = CSharpClassEmitter.Emit(new DbSchema { Tables = new List<DbTable> { orders } }, "MyApp.Data")["Order.cs"];

        Assert.That(src, Does.Contain("[ForeignKey(typeof(User)"));
        Assert.That(src, Does.Contain("Keys = [nameof(UserId)]"));
        Assert.That(src, Does.Contain("TargetKeys = [nameof(User.Id)]"));
    }

    // Regression: a scaffolded class dropped every UNIQUE constraint (the emitter only handled FKs), so a
    // scaffold→generate round-trip emitted a DROP CONSTRAINT and silently lost uniqueness. A single-column unique
    // must emit a property-level [Unique] (the form the analyzer reads back).
    [Test]
    public void Emits_Unique_For_SingleColumnUniqueConstraint()
    {
        var t = new DbTable
        {
            Name = "accounts",
            SourceName = "Account",
            Columns = new List<DbColumn>
            {
                new DbColumn { Name = "id", SourceName = "Id", DotnetType = "Guid", Nullable = false, IsPrimaryKey = true },
                new DbColumn { Name = "email", SourceName = "Email", DotnetType = "string", Nullable = false },
                new DbColumn { Name = "name", SourceName = "Name", DotnetType = "string", Nullable = false },
            },
            Constraints = new List<DbConstraint>
            {
                new DbConstraint { Type = DbConstraint.Types.Unique, TableName = "accounts", Columns = new[] { "email" } },
            }
        };

        var src = CSharpClassEmitter.Emit(new DbSchema { Tables = new List<DbTable> { t } }, "MyApp.Data")["Account.cs"];

        // The Email property carries [Unique]; Name (no unique constraint) does not.
        Assert.That(src, Does.Match(@"\[Unique\]\s*\r?\n\s*public string Email"));
        Assert.That(src, Does.Not.Match(@"\[Unique\]\s*\r?\n\s*public string Name"));
    }

    // A composite (multi-column) UNIQUE maps to a class-level [Unique(nameof(A), nameof(B))] using property names,
    // the form the analyzer reads back — so it round-trips instead of being dropped.
    [Test]
    public void Emits_ClassLevelUnique_For_CompositeUniqueConstraint()
    {
        var t = new DbTable
        {
            Name = "memberships",
            SourceName = "Membership",
            Columns = new List<DbColumn>
            {
                new DbColumn { Name = "id", SourceName = "Id", DotnetType = "Guid", Nullable = false, IsPrimaryKey = true },
                new DbColumn { Name = "org_id", SourceName = "OrgId", DotnetType = "Guid", Nullable = false },
                new DbColumn { Name = "user_id", SourceName = "UserId", DotnetType = "Guid", Nullable = false },
            },
            Constraints = new List<DbConstraint>
            {
                new DbConstraint { Type = DbConstraint.Types.Unique, TableName = "memberships", Columns = new[] { "org_id", "user_id" } },
            }
        };

        var src = CSharpClassEmitter.Emit(new DbSchema { Tables = new List<DbTable> { t } }, "MyApp.Data")["Membership.cs"];

        // Class-level [Unique(nameof(OrgId), nameof(UserId))], mapped from the DB column names to property names.
        Assert.That(src, Does.Match(@"\[Unique\(nameof\(OrgId\), nameof\(UserId\)\)\]\s*\r?\n\s*public partial class Membership"));
        // The composite columns must NOT also carry a property-level [Unique].
        Assert.That(src, Does.Not.Match(@"\[Unique\]\s*\r?\n\s*public Guid OrgId"));
    }
}
