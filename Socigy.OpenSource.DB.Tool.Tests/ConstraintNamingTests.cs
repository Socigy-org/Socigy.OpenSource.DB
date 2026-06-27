using System.Text.RegularExpressions;
using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using static Socigy.OpenSource.DB.Tool.Tests.TestSchema;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// The DROP CONSTRAINT a migration's DOWN script emits must name the exact constraint its UP script created,
/// otherwise rollback silently no-ops (DROP ... IF EXISTS) and leaves the constraint in place. Names must also
/// be reproducible across regenerations so re-running the tool produces identical migrations.
/// </summary>
[TestFixture]
public class ConstraintNamingTests
{
    private static (IReadOnlyList<string> Up, IReadOnlyList<string> Down) Generate(SchemaDiff diff)
    {
        var (up, down) = new PostgreSqlGenerator().Generate(diff, isFirstMigration: false);
        return (up.ToList(), down.ToList());
    }

    private static IEnumerable<string> Names(IEnumerable<string> sql, string verb) =>
        sql.SelectMany(s => Regex.Matches(s, verb + @" CONSTRAINT (?:IF EXISTS )?""([^""]+)""")
                                 .Select(m => m.Groups[1].Value));

    [Test]
    public void Added_table_FK_drop_in_down_matches_add_in_up()
    {
        var users = Table("users", Col("id", "uuid", pk: true));
        var orders = Table("orders", Col("id", "uuid", pk: true), Col("user_id", "uuid"));
        orders.Constraints!.Add(ForeignKey("orders", "user_id", "users", "id"));
        UseSchema(users, orders);

        var (up, down) = Generate(new SchemaDiff { AddedTables = { orders } });

        var added = Names(up, "ADD").ToHashSet();
        var dropped = Names(down, "DROP").ToHashSet();

        Assert.That(added, Is.Not.Empty, "expected an ADD CONSTRAINT for the FK");
        Assert.That(dropped, Is.EquivalentTo(added),
            $"DOWN drops {string.Join(",", dropped)} but UP added {string.Join(",", added)}");
    }

    [Test]
    public void Check_constraint_without_columns_has_a_reproducible_name()
    {
        DbTable Make()
        {
            var t = Table("orders", Col("id", "uuid", pk: true), Col("total", "integer"));
            t.Constraints!.Add(new DbConstraint
            {
                Type = DbConstraint.Types.Check,
                TableName = "orders",
                Value = "\"total\" >= 0",
                Columns = null, // raw-expression CHECK with no column list
            });
            return t;
        }

        var first = Generate(new SchemaDiff { AddedTables = { Make() } });
        var second = Generate(new SchemaDiff { AddedTables = { Make() } });

        Assert.That(second.Up, Is.EqualTo(first.Up),
            "a CHECK constraint with no column list must get a stable, reproducible name across runs");
    }

    // Two CHECKs on the SAME column (e.g. [Min(5)] + [Max(100)]) must get DISTINCT names; naming a check from its
    // columns alone collapsed them to one name, so the CREATE TABLE emitted two same-named constraints (apply fails
    // with "constraint already exists").
    [Test]
    public void Multiple_checks_on_same_column_get_distinct_names()
    {
        var t = Table("users", Col("id", "uuid", pk: true), Col("age", "integer"));
        t.Constraints!.Add(new DbConstraint { Type = DbConstraint.Types.Check, TableName = "users", Columns = new[] { "age" }, Value = "\"age\" >= 5" });
        t.Constraints!.Add(new DbConstraint { Type = DbConstraint.Types.Check, TableName = "users", Columns = new[] { "age" }, Value = "\"age\" <= 100" });
        UseSchema(t);

        var (up, _) = Generate(new SchemaDiff { AddedTables = { t } });
        string sql = string.Join("\n", up);

        var checkNames = Regex.Matches(sql, @"CONSTRAINT ""([^""]+)"" CHECK").Select(m => m.Groups[1].Value).ToList();
        Assert.That(checkNames, Has.Count.EqualTo(2), "both checks must be emitted");
        Assert.That(checkNames.Distinct().Count(), Is.EqualTo(2),
            $"two checks on the same column must have distinct names, got: {string.Join(", ", checkNames)}");
    }

    // The distinct names must also be STABLE across regenerations (folded from the check expression, not random).
    [Test]
    public void Multiple_checks_on_same_column_names_are_reproducible()
    {
        DbTable Make()
        {
            var t = Table("users", Col("id", "uuid", pk: true), Col("age", "integer"));
            t.Constraints!.Add(new DbConstraint { Type = DbConstraint.Types.Check, TableName = "users", Columns = new[] { "age" }, Value = "\"age\" >= 5" });
            t.Constraints!.Add(new DbConstraint { Type = DbConstraint.Types.Check, TableName = "users", Columns = new[] { "age" }, Value = "\"age\" <= 100" });
            return t;
        }

        var first = Generate(new SchemaDiff { AddedTables = { Make() } });
        var second = Generate(new SchemaDiff { AddedTables = { Make() } });
        Assert.That(second.Up, Is.EqualTo(first.Up), "per-check names must be stable across runs");
    }

    [Test]
    public void Generation_is_reproducible_across_runs()
    {
        DbTable Make()
        {
            var t = Table("orders", Col("id", "uuid", pk: true), Col("user_id", "uuid"));
            t.Constraints!.Add(ForeignKey("orders", "user_id", "users", "id"));
            return t;
        }

        UseSchema(Table("users", Col("id", "uuid", pk: true)), Make());
        var first = Generate(new SchemaDiff { AddedTables = { Make() } });

        UseSchema(Table("users", Col("id", "uuid", pk: true)), Make());
        var second = Generate(new SchemaDiff { AddedTables = { Make() } });

        Assert.That(second.Up, Is.EqualTo(first.Up), "UP script must be identical across regenerations");
        Assert.That(second.Down, Is.EqualTo(first.Down), "DOWN script must be identical across regenerations");
    }
}
