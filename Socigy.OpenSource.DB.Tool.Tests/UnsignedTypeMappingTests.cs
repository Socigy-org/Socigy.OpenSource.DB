using Socigy.OpenSource.DB.Tool.Generators;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Unsigned CLR integer types have no native PostgreSQL type, so the runtime widens them on read/write
/// (ushort→integer, uint→bigint, ulong→numeric, sbyte→smallint). The migration DDL mapping MUST match that
/// widening; before the fix it fell through to the raw .NET name (e.g. "uint32"), producing invalid CREATE
/// TABLE DDL that fails to apply and diverges from the type inserts/reads actually use.
/// </summary>
[TestFixture]
public class UnsignedTypeMappingTests
{
    private readonly PostgreSqlGenerator _gen = new();

    [TestCase("System.UInt16", "integer")]
    [TestCase("System.UInt32", "bigint")]
    [TestCase("System.UInt64", "numeric")]
    [TestCase("System.SByte", "smallint")]
    [TestCase("ushort", "integer")]
    [TestCase("uint", "bigint")]
    [TestCase("ulong", "numeric")]
    [TestCase("sbyte", "smallint")]
    public void Unsigned_MapsToWidenedRuntimeType(string clr, string expectedDdl)
    {
        Assert.That(_gen.GetDatabaseType(clr), Is.EqualTo(expectedDdl),
            $"{clr} DDL type must match the runtime widening, not the raw .NET name");
    }

    [Test]
    public void Unsigned_NeverFallsThroughToRawDotnetName()
    {
        foreach (var raw in new[] { "uint32", "uint64", "uint16" })
            Assert.That(_gen.GetDatabaseType(raw), Does.Not.Contain("uint"),
                "the raw .NET type name is not a valid PostgreSQL type");
    }
}
