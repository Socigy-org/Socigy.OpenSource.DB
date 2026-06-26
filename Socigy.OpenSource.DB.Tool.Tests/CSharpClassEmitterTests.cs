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
}
