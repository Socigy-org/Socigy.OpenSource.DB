using Socigy.OpenSource.DB.Tool.Introspection;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>Unit tests for the PG-type -> C#-type mapping used by database-first scaffolding.</summary>
[TestFixture]
public class InverseTranslatorTests
{
    // character(n>1) is a fixed-length string; mapping it to a single C# char truncates. Only n==1 is a char.
    [Test]
    public void CharN_WidensToString_ButChar1_StaysChar()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PostgresInverseTranslator.PgTypeToCSharp("character", "bpchar", 1), Is.EqualTo("char"));
            Assert.That(PostgresInverseTranslator.PgTypeToCSharp("character", "bpchar", 3), Is.EqualTo("string"));
            Assert.That(PostgresInverseTranslator.PgTypeToCSharp("char", "bpchar", 10), Is.EqualTo("string"));
        });
    }

    [Test]
    public void CommonTypes_MapAsExpected()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PostgresInverseTranslator.PgTypeToCSharp("integer", "int4"), Is.EqualTo("int"));
            Assert.That(PostgresInverseTranslator.PgTypeToCSharp("timestamp with time zone", "timestamptz"), Is.EqualTo("DateTimeOffset"));
            Assert.That(PostgresInverseTranslator.PgTypeToCSharp("uuid", "uuid"), Is.EqualTo("Guid"));
            Assert.That(PostgresInverseTranslator.PgTypeToCSharp("character varying", "varchar", 100), Is.EqualTo("string"));
        });
    }
}
