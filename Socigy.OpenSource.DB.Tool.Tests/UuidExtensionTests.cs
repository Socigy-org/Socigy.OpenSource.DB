using System.Linq;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using static Socigy.OpenSource.DB.Tool.Tests.TestSchema;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// A <c>Guid.Sequential</c> default translates to <c>uuid_generate_v1mc()</c>, which lives in the <c>uuid-ossp</c>
/// extension. The generated migration must ensure that extension first, or it fails to apply with
/// "function uuid_generate_v1mc() does not exist". <c>Guid.Random</c> (<c>gen_random_uuid()</c>) is built in and
/// must NOT pull in the extension.
/// </summary>
[TestFixture]
public class UuidExtensionTests
{
    private static IReadOnlyList<string> Up(DbTable added)
    {
        UseSchema(added);
        var (up, _) = new PostgreSqlGenerator().Generate(new SchemaDiff { AddedTables = { added } }, isFirstMigration: false);
        return up.ToList();
    }

    [Test]
    public void SequentialGuidDefault_EmitsUuidOsspExtensionFirst()
    {
        var t = Table("things", Col("id", "uuid", pk: true, defaultValue: DbDefaults.Guid.Sequential, dotnetType: "Guid"));
        var up = Up(t);

        Assert.That(up.Any(s => s.Contains("uuid_generate_v1mc()")), "expected the sequential-uuid default in the DDL");
        Assert.That(up[0], Does.Contain("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\""),
            "the extension must be created before the statement that uses it");
    }

    [Test]
    public void RandomGuidDefault_DoesNotEmitExtension()
    {
        var t = Table("things", Col("id", "uuid", pk: true, defaultValue: DbDefaults.Guid.Random, dotnetType: "Guid"));
        var up = Up(t);

        Assert.That(up.Any(s => s.Contains("gen_random_uuid()")), "expected the random-uuid built-in default");
        Assert.That(up.Any(s => s.Contains("uuid-ossp")), Is.False, "gen_random_uuid is built in; no extension needed");
    }
}
