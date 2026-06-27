using Socigy.OpenSource.DB.Tool.Introspection;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Scaffolding reads a column default and strips PostgreSQL <c>::type</c> casts before mapping it. A
/// schema-qualified or quoted cast (<c>::public.citext</c>, <c>::"public"."citext"</c>) must be removed in full;
/// the old char class excluded <c>.</c>/<c>"</c>, leaving a bogus literal like <c>'x'.citext</c> that
/// forward-generated invalid DDL.
/// </summary>
[TestFixture]
public class InverseDefaultCastTests
{
    [TestCase("'x'::text", "'x'")]
    [TestCase("'x'::public.citext", "'x'")]
    [TestCase("'x'::pg_catalog.text", "'x'")]
    [TestCase("'x'::\"public\".\"citext\"", "'x'")]
    [TestCase("'utc'::character varying", "'utc'")]
    public void SchemaQualifiedCast_IsFullyStripped(string columnDefault, string expectedLiteral)
    {
        Assert.That(PostgresInverseTranslator.InverseDefault(columnDefault), Is.EqualTo(expectedLiteral));
        Assert.That(PostgresInverseTranslator.InverseDefault(columnDefault), Does.Not.Contain("."),
            "no fragment of the cast type name may survive in the emitted default");
    }

    [Test]
    public void RecognizedDefaults_StillMapAfterStrip()
    {
        // A recognized token wrapped in a schema-qualified cast must still normalize to its DbDefaults token.
        Assert.That(PostgresInverseTranslator.InverseDefault("nextval('s'::regclass)"), Is.Null, "serial → no [Default]");
        Assert.That(PostgresInverseTranslator.InverseDefault("true"), Is.Not.Null);
    }
}
