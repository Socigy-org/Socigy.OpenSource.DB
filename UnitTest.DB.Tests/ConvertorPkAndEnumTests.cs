using UnitTest.DB;

namespace UnitTest.DB.Tests;

/// <summary>
/// Live tests for value-convertor edge cases on the write path:
///  - a convertor on the PRIMARY KEY must be applied to the UPDATE/DELETE WHERE clause (regression: it wasn't,
///    so the WHERE bound the raw value and matched no rows);
///  - an enum routed through a custom string-returning convertor must not crash the UPDATE coercion.
/// </summary>
[TestFixture]
public class ConvertorPkAndEnumTests : BaseUnitTest
{
    [SetUp]
    public async Task Clean()
    {
        await ClearAsync("test_convertor_pk_items");
        await ClearAsync("test_enum_convertor_items");
        await ClearAsync("test_enum_pk_convertor_items");
    }

    // ── Bug 2: convertor on the PK feeds the WHERE ──

    [Test]
    public async Task Update_ByConvertorPk_MatchesTheConvertedValue()
    {
        // Insert "abc" -> the UpperCaseStringConvertor stores "ABC".
        await new TestConvertorPkItem { Code = "abc", Note = "n1" }
            .Insert().WithConnection(Connection).ExecuteAsync();

        // Update by a fresh instance whose Code is the PRE-conversion "abc". The WHERE must convert it to "ABC"
        // to match the stored row; before the fix it bound "abc" and updated 0 rows.
        int rows = await new TestConvertorPkItem { Code = "abc", Note = "n2" }
            .Update().WithConnection(Connection).WithAllFields().ExecuteAsync();

        Assert.That(rows, Is.EqualTo(1), "WHERE on a convertor PK must bind the converted value");

        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT note FROM test_convertor_pk_items WHERE code = 'ABC'";
        Assert.That(await cmd.ExecuteScalarAsync() as string, Is.EqualTo("n2"));
    }

    [Test]
    public async Task Delete_ByConvertorPk_MatchesTheConvertedValue()
    {
        await new TestConvertorPkItem { Code = "abc", Note = "n1" }
            .Insert().WithConnection(Connection).ExecuteAsync();

        int rows = await new TestConvertorPkItem { Code = "abc" }
            .Delete().WithConnection(Connection).ExecuteAsync();

        Assert.That(rows, Is.EqualTo(1));
    }

    // Regression: DELETE-by-instance forced NpgsqlDbType from the DECLARED PK type, so a type-changing convertor
    // PK (enum WorkStatus → string) had its string value forced to Integer and Npgsql threw — while the identical
    // UPDATE-by-instance succeeded. DELETE must mirror UPDATE: bind the converted value and let Npgsql infer.
    [Test]
    public async Task Delete_ByTypeChangingConvertorPk_MatchesTheConvertedValue()
    {
        await new TestEnumPkConvertorItem { Status = WorkStatus.Active, Note = "n1" }
            .Insert().WithConnection(Connection).ExecuteAsync();

        int rows = await new TestEnumPkConvertorItem { Status = WorkStatus.Active }
            .Delete().WithConnection(Connection).ExecuteAsync();

        Assert.That(rows, Is.EqualTo(1), "DELETE on an enum→string convertor PK must bind the converted string value");
    }

    // The UPDATE counterpart must keep working too (confirms DELETE now matches UPDATE's behavior on this PK shape).
    [Test]
    public async Task Update_ByTypeChangingConvertorPk_MatchesTheConvertedValue()
    {
        await new TestEnumPkConvertorItem { Status = WorkStatus.Done, Note = "n1" }
            .Insert().WithConnection(Connection).ExecuteAsync();

        int rows = await new TestEnumPkConvertorItem { Status = WorkStatus.Done, Note = "n2" }
            .Update().WithConnection(Connection).WithAllFields().ExecuteAsync();

        Assert.That(rows, Is.EqualTo(1));
    }

    // ── Bug 3: UPDATE doesn't crash on an enum stored via a string-returning convertor ──

    [Test]
    public async Task Update_EnumWithStringConvertor_DoesNotCrash()
    {
        var id = Guid.NewGuid();
        await new TestEnumConvertorItem { Id = id, Status = WorkStatus.Pending }
            .Insert().WithConnection(Connection).ExecuteAsync();

        int rows = await new TestEnumConvertorItem { Id = id, Status = WorkStatus.Active }
            .Update().WithConnection(Connection).WithAllFields().ExecuteAsync();

        Assert.That(rows, Is.EqualTo(1));

        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"SELECT status FROM test_enum_convertor_items WHERE id = '{id}'";
        Assert.That(await cmd.ExecuteScalarAsync() as string, Is.EqualTo("Active"),
            "the enum must be stored as its string name via the convertor");
    }

    // Binary COPY went through the same declared-type enum coercion and threw FormatException on the convertor's
    // string output. The COPY bridge now coerces only when the runtime value is actually an enum.
    [Test]
    public async Task BulkCopy_EnumWithStringConvertor_DoesNotCrash()
    {
        var rows = new[]
        {
            new TestEnumConvertorItem { Id = Guid.NewGuid(), Status = WorkStatus.Active },
            new TestEnumConvertorItem { Id = Guid.NewGuid(), Status = WorkStatus.Done },
        };

        ulong written = await global::Socigy.OpenSource.DB.Core.Bulk.BulkCopy.InsertMultipleCopyAsync(rows, Connection);
        Assert.That(written, Is.EqualTo(2u));

        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM test_enum_convertor_items WHERE status IN ('Active','Done')";
        Assert.That(Convert.ToInt64(await cmd.ExecuteScalarAsync()), Is.EqualTo(2));
    }
}
