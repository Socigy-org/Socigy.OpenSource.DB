using System.Linq;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Forward analysis of enum tables and flagged-enum junctions. An enum member without a [Description] seeds the
/// description column with NULL, so that column must be nullable; and a nullable flagged-enum property must not
/// crash the tool (the same wrapped-type pitfall as the single-enum-FK path).
/// </summary>
[TestFixture]
public class AssemblyAnalyzerEnumTableTests
{
    [Test]
    public void EnumTable_DescriptionColumn_IsNullable()
    {
        const string model = @"
using Socigy.OpenSource.DB.Attributes;
namespace Fixture
{
    [Table(""roles"")] public enum Role { Admin, User }
}";
        var schema = AnalyzerModelCompiler.Analyze(model);

        var roles = schema.Tables.FirstOrDefault(t => t.Name == "roles");
        Assert.That(roles, Is.Not.Null, "the [Table] enum must produce an enum table");

        var description = roles!.Columns.First(c => c.Name == "description");
        Assert.That(description.Nullable, Is.True,
            "description is optional (an undescribed member seeds NULL); a NOT NULL description fails the seed INSERT");

        // value is always the member name, so it stays required.
        var value = roles.Columns.First(c => c.Name == "value");
        Assert.That(value.Nullable, Is.Not.EqualTo(true));

        // The seed for an undescribed member carries a null description (the case that needs the nullable column).
        var seed = roles.InstantiatedValues!.First(r => (string)r["value"]! == "Admin");
        Assert.That(seed["description"], Is.Null);
    }

    [Test]
    public void NullableFlaggedEnum_DoesNotCrash_AndCreatesJunction()
    {
        const string model = @"
using System;
using Socigy.OpenSource.DB.Attributes;
namespace Fixture
{
    [Flags][Table(""roles"")] public enum Role { A = 1, B = 2 }

    [Table(""users"")] public partial class User
    {
        [PrimaryKey] public Guid Id { get; set; }
        [FlaggedEnum] public Role? Roles { get; set; }
    }
}";
        var schema = AnalyzerModelCompiler.Analyze(model);

        Assert.That(schema.Tables.Any(t => t.Name == "users_roles"), Is.True,
            "a nullable [FlaggedEnum] property must still produce its junction table (it previously crashed the tool)");
    }
}
