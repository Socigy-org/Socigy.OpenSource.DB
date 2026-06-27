using Socigy.OpenSource.DB.Tool.Introspection;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// The schema reader must reconstruct a column's DB type string so it round-trips against the forward map. A
/// fixed-length character column must carry its length (CLR char → "character(1)"); a bare "character" produced a
/// spurious Type change on every scaffold→generate. varchar and numeric(p,s) must likewise keep their parameters.
/// </summary>
[TestFixture]
public class SchemaReaderTypeTests
{
    [TestCase("character", 1, "character(1)")]
    [TestCase("char", 1, "character(1)")]
    [TestCase("character varying", 50, "character varying(50)")]
    public void FixedAndVaryingCharacter_KeepLength(string dataType, int maxLength, string expected)
    {
        Assert.That(PostgresSchemaReader.BuildDatabaseType(dataType, maxLength, null, null), Is.EqualTo(expected));
    }

    // An UNBOUNDED varchar (no length) must map to text — a scaffolded `string` regenerates as text, so returning
    // the raw "character varying" reported a spurious (data-touching) ALTER ... TYPE text on every round-trip.
    [TestCase("character varying")]
    [TestCase("varchar")]
    public void UnboundedVarchar_MapsToText(string dataType)
    {
        Assert.That(PostgresSchemaReader.BuildDatabaseType(dataType, null, null, null), Is.EqualTo("text"));
    }

    [Test]
    public void Numeric_KeepsPrecisionAndScale()
    {
        Assert.That(PostgresSchemaReader.BuildDatabaseType("numeric", null, 10, 2), Is.EqualTo("numeric(10,2)"));
        Assert.That(PostgresSchemaReader.BuildDatabaseType("numeric", null, null, null), Is.EqualTo("numeric"));
    }

    [Test]
    public void PlainTypes_PassThrough()
    {
        Assert.That(PostgresSchemaReader.BuildDatabaseType("integer", null, null, null), Is.EqualTo("integer"));
        Assert.That(PostgresSchemaReader.BuildDatabaseType("uuid", null, null, null), Is.EqualTo("uuid"));
    }
}
