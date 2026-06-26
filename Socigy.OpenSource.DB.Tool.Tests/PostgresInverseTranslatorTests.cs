using NUnit.Framework;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool.Introspection;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// The inverse translators must be the exact inverse of <c>PostgreSqlGenerator</c>'s forward maps, otherwise
/// DB-first scaffolding wouldn't round-trip back to the same schema. These lock the mapping in both directions.
/// </summary>
[TestFixture]
public class PostgresInverseTranslatorTests
{
    [TestCase("gen_random_uuid()", DbDefaults.Guid.Random)]
    [TestCase("uuid_generate_v1mc()", DbDefaults.Guid.Sequential)]
    [TestCase("timezone('utc', now())", DbDefaults.Time.Now)]
    [TestCase("now()", DbDefaults.Time.NowLocal)]
    [TestCase("current_date", DbDefaults.Time.Date)]
    [TestCase("true", DbDefaults.Bool.True)]
    [TestCase("false", DbDefaults.Bool.False)]
    [TestCase("0", DbDefaults.Number.Zero)]
    [TestCase("1", DbDefaults.Number.One)]
    [TestCase("''", DbDefaults.Text.Empty)]
    public void InverseDefault_MapsKnownExpressions(string expr, string token)
        => Assert.That(PostgresInverseTranslator.InverseDefault(expr), Is.EqualTo(token));

    [Test]
    public void InverseDefault_StripsCasts()
        => Assert.That(PostgresInverseTranslator.InverseDefault("timezone('utc'::text, now())"),
                       Is.EqualTo(DbDefaults.Time.Now));

    [Test]
    public void InverseDefault_NextvalIsAutoIncrement_ReturnsNull()
        => Assert.That(PostgresInverseTranslator.InverseDefault("nextval('users_id_seq'::regclass)"), Is.Null);

    [Test]
    public void InverseDefault_UnknownLiteral_PassesThrough()
        => Assert.That(PostgresInverseTranslator.InverseDefault("'active'::text"), Is.EqualTo("'active'"));

    [TestCase('c', DbValues.ForeignKey.Cascade)]
    [TestCase('n', DbValues.ForeignKey.SetNull)]
    [TestCase('d', DbValues.ForeignKey.SetDefault)]
    [TestCase('r', DbValues.ForeignKey.Restrict)]
    [TestCase('a', DbValues.ForeignKey.NoAction)]
    public void InverseForeignKeyAction_MapsCodes(char code, string token)
        => Assert.That(PostgresInverseTranslator.InverseForeignKeyAction(code), Is.EqualTo(token));

    [TestCase("integer", null, "int")]
    [TestCase("bigint", null, "long")]
    [TestCase("smallint", null, "short")]
    [TestCase("numeric", null, "decimal")]
    [TestCase("boolean", null, "bool")]
    [TestCase("uuid", null, "Guid")]
    [TestCase("bytea", null, "byte[]")]
    [TestCase("jsonb", null, "string")]
    [TestCase("text", null, "string")]
    [TestCase("character varying", "varchar", "string")]
    [TestCase("timestamp without time zone", null, "DateTime")]
    [TestCase("timestamp with time zone", null, "DateTimeOffset")]
    public void PgTypeToCSharp_Maps(string dataType, string? udt, string expected)
        => Assert.That(PostgresInverseTranslator.PgTypeToCSharp(dataType, udt), Is.EqualTo(expected));
}
