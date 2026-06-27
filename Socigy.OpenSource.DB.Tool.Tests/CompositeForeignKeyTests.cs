using System.Linq;
using System.Text.RegularExpressions;
using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using static Socigy.OpenSource.DB.Tool.Tests.TestSchema;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// A composite foreign key (a `[FlaggedEnum]` junction back to a composite-PK main table) must render as ONE
/// multi-column <c>FOREIGN KEY (a, b) REFERENCES t (x, y)</c> — not N single-column FKs, which fail at apply with
/// "no unique constraint matching given keys" because no individual PK column is unique on its own.
/// </summary>
[TestFixture]
public class CompositeForeignKeyTests
{
    [Test]
    public void Composite_ForeignKey_RendersAsOneMultiColumnClause()
    {
        var junction = Table("memberships_roles",
            Col("membership_tenant", "uuid", pk: true),
            Col("membership_id", "uuid", pk: true),
            Col("role_id", "integer", pk: true));
        junction.Constraints!.Add(new DbConstraint
        {
            Type = DbConstraint.Types.ForeignKey,
            TableName = "memberships_roles",
            Columns = new[] { "membership_tenant", "membership_id" },
            TargetTable = "memberships",
            TargetColumns = new[] { "tenant", "id" },
            OnDelete = "CASCADE",
        });
        UseSchema(junction, Table("memberships", Col("tenant", "uuid", pk: true), Col("id", "uuid", pk: true)));

        var (up, _) = new PostgreSqlGenerator().Generate(new SchemaDiff { AddedTables = { junction } }, isFirstMigration: false);
        string sql = string.Join("\n", up);

        var fkClauses = Regex.Matches(sql, "FOREIGN KEY \\([^)]*\\)").Select(m => m.Value).ToList();
        Assert.That(fkClauses, Has.Count.EqualTo(1), "a composite FK must be exactly one FOREIGN KEY clause, not N");
        Assert.That(fkClauses[0], Does.Contain("\"membership_tenant\"").And.Contain("\"membership_id\""),
            "the single FK must reference both key columns");
    }
}
