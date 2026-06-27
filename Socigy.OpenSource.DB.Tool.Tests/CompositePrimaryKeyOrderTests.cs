using System.Linq;
using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Scaffolding;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using static Socigy.OpenSource.DB.Tool.Tests.TestSchema;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// A composite primary key whose key order differs from the column declaration order must round-trip. The DB
/// reader records each PK column's key position as <see cref="DbColumn.PrimaryKeyOrder"/>; the generator emits the
/// <c>PRIMARY KEY (...)</c> in that order, and the C# emitter writes <c>[PrimaryKey(order)]</c> so a re-analyze
/// preserves it. Previously the order was discarded and the key was emitted in column order.
/// </summary>
[TestFixture]
public class CompositePrimaryKeyOrderTests
{
    // Table columns declared (a, b) but the PK key order is (b, a): b has PrimaryKeyOrder 0, a has 1.
    private static DbTable OutOfOrderCompositePkTable()
    {
        var table = Table("pairs",
            Col("a", "integer", pk: true, dotnetType: "int"),
            Col("b", "integer", pk: true, dotnetType: "int"));
        table.SourceName = "Pair";
        table.Columns[0].SourceName = "A"; table.Columns[0].PrimaryKeyOrder = 1;
        table.Columns[1].SourceName = "B"; table.Columns[1].PrimaryKeyOrder = 0;
        return table;
    }

    [Test]
    public void Generator_EmitsPrimaryKeyInKeyOrder_NotColumnOrder()
    {
        var table = OutOfOrderCompositePkTable();
        UseSchema(table);

        var (up, _) = new PostgreSqlGenerator().Generate(new SchemaDiff { AddedTables = { table } }, isFirstMigration: false);
        string sql = string.Join("\n", up);

        // Key order is (b, a) per PrimaryKeyOrder, despite columns declared (a, b).
        Assert.That(sql, Does.Contain("PRIMARY KEY (\"b\", \"a\")"));
        Assert.That(sql, Does.Not.Contain("PRIMARY KEY (\"a\", \"b\")"));
    }

    [Test]
    public void Emitter_WritesPrimaryKeyOrder_ForCompositeKey()
    {
        var table = OutOfOrderCompositePkTable();
        var src = CSharpClassEmitter.Emit(new DbSchema { Tables = new List<DbTable> { table } }, "MyApp.Data")["Pair.cs"];

        // A carries [PrimaryKey(1)], B carries [PrimaryKey(0)] — the key position, so a re-analyze preserves order.
        Assert.That(src, Does.Match(@"\[PrimaryKey\(1\)\]\s*\r?\n\s*public int A"));
        Assert.That(src, Does.Match(@"\[PrimaryKey\(0\)\]\s*\r?\n\s*public int B"));
    }

    [Test]
    public void SingleColumnPrimaryKey_StaysBare()
    {
        var t = Table("t", Col("id", "uuid", pk: true, dotnetType: "Guid"));
        var src = CSharpClassEmitter.Emit(new DbSchema { Tables = new List<DbTable> { t } }, "MyApp.Data")["t.cs"];
        // A single-column PK is not composite, so it stays a bare [PrimaryKey] (no order argument).
        Assert.That(src, Does.Contain("[PrimaryKey]"));
        Assert.That(src, Does.Not.Contain("[PrimaryKey("));
    }
}
