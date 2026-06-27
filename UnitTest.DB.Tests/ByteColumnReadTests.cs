using UnitTest.DB;

namespace UnitTest.DB.Tests;

/// <summary>
/// A non-enum byte/sbyte column is stored as smallint (int2). Npgsql has no int2→byte/sbyte reader handler, so the
/// default fast read path (ReadScalar) must narrow from short. Before the fix it called GetFieldValue&lt;byte&gt;
/// directly and threw InvalidCastException, so the row never materialized — while the slow/DTO paths worked.
/// </summary>
[TestFixture]
public class ByteColumnReadTests : BaseUnitTest
{
    [SetUp]
    public Task Clean() => ClearAsync("test_types");

    [Test]
    public async Task ByteAndSByte_Columns_RoundTripThroughDefaultReadPath()
    {
        var id = Guid.NewGuid();
        // 200 > sbyte/byte midpoints and SignedByte negative — values that exercise real narrowing.
        await new TestType { Id = id, SmallByte = 200, SignedByte = -100 }
            .Insert().WithConnection(Connection).ExecuteAsync();

        // ToListAsync uses the default fast read path (ConvertFrom(reader, ordinals) -> ReadScalar).
        var rows = await TestType.Query(x => x.Id == id).WithConnection(Connection).ExecuteAsync().ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1), "the row must materialize (a byte column previously threw on read)");
        Assert.That(rows[0].SmallByte, Is.EqualTo((byte)200));
        Assert.That(rows[0].SignedByte, Is.EqualTo((sbyte)-100));
    }

    [Test]
    public async Task UlongBackedEnum_Column_RoundTripsThroughDefaultReadPath()
    {
        var id = Guid.NewGuid();
        // High (10_000_000_000) exceeds uint range, so it only fits a ulong — stored as NUMERIC, which Npgsql
        // returns as a boxed decimal. The fast ordinal path passed that to Enum.ToObject, which rejects a decimal.
        await new TestType { Id = id, Big = BigStatus.High }
            .Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await TestType.Query(x => x.Id == id).WithConnection(Connection).ExecuteAsync().ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1), "the row must materialize (a ulong-backed enum previously threw on read)");
        Assert.That(rows[0].Big, Is.EqualTo(BigStatus.High));
    }
}
