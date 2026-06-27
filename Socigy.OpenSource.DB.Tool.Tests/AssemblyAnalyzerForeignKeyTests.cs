using System.Linq;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Forward model-analysis (AssemblyAnalyzer) regressions for ordinary hand-written models: an optional FK to an
/// enum table must not crash the tool, and an explicit property-level [ForeignKey(TargetKeys=...)] must be honored.
/// </summary>
[TestFixture]
public class AssemblyAnalyzerForeignKeyTests
{
    // A `MyEnum? Prop` optional enum-table FK passed the still-wrapped Nullable<TEnum> into the enum-table lookup,
    // which looked for [Table] on Nullable<T> and Environment.Exit(-1) — crashing the whole tool. It must analyze.
    [Test]
    public void NullableEnumTableForeignKey_DoesNotCrash_AndCreatesFk()
    {
        const string model = @"
using System;
using Socigy.OpenSource.DB.Attributes;

namespace Fixture
{
    [Table(""colors"")]
    public enum Color { Red, Green, Blue }

    [Table(""users"")]
    public partial class User
    {
        [PrimaryKey] public Guid Id { get; set; }
        public Color? FavoriteColor { get; set; }
    }
}";
        var schema = AnalyzerModelCompiler.Analyze(model);

        var users = schema.Tables.FirstOrDefault(t => t.Name == "users");
        Assert.That(users, Is.Not.Null, "the users table must be analyzed (the tool previously aborted here)");
        var fk = users!.Constraints?.FirstOrDefault(c => c.Type == DbConstraint.Types.ForeignKey);
        Assert.That(fk, Is.Not.Null, "an optional enum-table property must still produce a foreign key");
    }

    // Property-level [ForeignKey(TargetKeys=[...])] was read with the wrong cast (as IEnumerable<string>), which is
    // always null under MetadataLoadContext, so the explicit target keys were dropped and auto-resolved to the
    // target's primary key instead. The requested target column must be honored.
    [Test]
    public void PropertyForeignKey_TargetKeys_AreHonored()
    {
        const string model = @"
using System;
using Socigy.OpenSource.DB.Attributes;

namespace Fixture
{
    [Table(""accounts"")]
    public partial class Account
    {
        [PrimaryKey] public Guid Id { get; set; }
        [Unique] public string Email { get; set; } = """";
    }

    [Table(""orders"")]
    public partial class Order
    {
        [PrimaryKey] public Guid Id { get; set; }
        [ForeignKey(typeof(Account), TargetKeys = new[] { nameof(Account.Email) })]
        public string OwnerEmail { get; set; } = """";
    }
}";
        var schema = AnalyzerModelCompiler.Analyze(model);

        var orders = schema.Tables.FirstOrDefault(t => t.Name == "orders");
        Assert.That(orders, Is.Not.Null);
        var fk = orders!.Constraints?.FirstOrDefault(c => c.Type == DbConstraint.Types.ForeignKey);
        Assert.That(fk, Is.Not.Null, "the property-level FK must be produced");
        Assert.That(fk!.TargetColumns, Is.Not.Null);
        Assert.That(fk.TargetColumns!.Select(c => c), Does.Contain("Email"),
            "the explicit TargetKeys (Email) must be honored, not auto-resolved to the target's PK");
        Assert.That(fk.TargetColumns!.Select(c => c), Does.Not.Contain("Id"),
            "the FK must not silently point at the target's primary key");
    }
}
