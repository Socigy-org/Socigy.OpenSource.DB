using UnitTest.DB;

namespace UnitTest.DB.Tests;

[TestFixture]
public class ValueConvertorTests : BaseUnitTest
{
    [SetUp]
    public async Task Clean() => await ClearAsync("test_convertor_items");

    // ------------------------------------------------------------------
    // Insert: convertor applies ConvertToDbValue before writing
    // ------------------------------------------------------------------

    [Test]
    public async Task Insert_WithConvertor_DbStoresConvertedValue()
    {
        var id = Guid.NewGuid();
        var item = new TestConvertorItem { Id = id, Label = "hello", Value = 1 };

        bool ok = await item.Insert()
            .WithConnection(Connection)
            .ExecuteAsync();

        Assert.That(ok, Is.True);

        // Read the raw label value directly from the DB (bypass the ORM model)
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"SELECT label FROM test_convertor_items WHERE id = '{id}'";
        var rawLabel = await cmd.ExecuteScalarAsync() as string;

        Assert.That(rawLabel, Is.EqualTo("HELLO"), "UpperCaseStringConvertor must uppercase before storing");
    }

    // ------------------------------------------------------------------
    // Query: convertor applies ConvertFromDbValue when reading back
    // ------------------------------------------------------------------

    [Test]
    public async Task Query_WithConvertor_ReturnedValueIsConverted()
    {
        var id = Guid.NewGuid();
        var item = new TestConvertorItem { Id = id, Label = "world", Value = 2 };
        await item.Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await TestConvertorItem.Query(x => x.Id == id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        // ConvertFromDbValue returns the stored string as-is ("WORLD" stored, "WORLD" returned)
        Assert.That(rows[0].Label, Is.EqualTo("WORLD"));
    }

    // ------------------------------------------------------------------
    // Update: convertor applies ConvertToDbValue on new value
    // ------------------------------------------------------------------

    [Test]
    public async Task Update_WithConvertor_DbStoresConvertedValue()
    {
        var id = Guid.NewGuid();
        var item = new TestConvertorItem { Id = id, Label = "initial", Value = 3 };
        await item.Insert().WithConnection(Connection).ExecuteAsync();

        item.Label = "updated";
        await item.Update()
            .WithConnection(Connection)
            .WithFields(x => new object[] { x.Label })
            .ExecuteAsync();

        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"SELECT label FROM test_convertor_items WHERE id = '{id}'";
        var rawLabel = await cmd.ExecuteScalarAsync() as string;

        Assert.That(rawLabel, Is.EqualTo("UPDATED"), "Updated label should be stored upper-cased");
    }

    // ------------------------------------------------------------------
    // Round-trip: value survives insert → query intact
    // ------------------------------------------------------------------

    [Test]
    public async Task RoundTrip_InsertAndQuery_MatchingConvertedValue()
    {
        var id = Guid.NewGuid();
        var item = new TestConvertorItem { Id = id, Label = "RoundTrip", Value = 42 };
        await item.Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await TestConvertorItem.Query(x => x.Id == id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows[0].Label, Is.EqualTo("ROUNDTRIP"));
        Assert.That(rows[0].Value, Is.EqualTo(42));
    }

    // ------------------------------------------------------------------
    // GetColumns metadata: ColumnInfo.Value reflects conversion
    // ------------------------------------------------------------------

    [Test]
    public void GetColumns_ConverterColumn_ValueIsConverted()
    {
        var item = new TestConvertorItem { Id = Guid.NewGuid(), Label = "metadata", Value = 0 };
        var cols = item.GetColumns();

        Assert.That(cols.ContainsKey(TestConvertorItem.LabelColumnName), Is.True);
        var labelCol = cols[TestConvertorItem.LabelColumnName];
        // ColumnInfo.Value should be the result of ConvertToDbValue, i.e. "METADATA"
        Assert.That(labelCol.Value, Is.EqualTo("METADATA"));
    }

    // ------------------------------------------------------------------
    // WHERE filter on a convertor column: the predicate value must be run through ConvertToDbValue too —
    // the column stores the converted form ("WORLD"), so binding the raw "world" would match no rows.
    // ------------------------------------------------------------------

    [Test]
    public async Task Query_FilterByConvertorColumn_Equal_MatchesConvertedValue()
    {
        var id = Guid.NewGuid();
        await new TestConvertorItem { Id = id, Label = "world", Value = 7 }
            .Insert().WithConnection(Connection).ExecuteAsync();

        // Filter by the RAW (un-converted) literal; the visitor must convert it to "WORLD" before binding.
        var rows = await TestConvertorItem.Query(x => x.Label == "world")
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows.Any(r => r.Id == id), Is.True,
            "filtering a convertor column by its raw value must match the converted row");
    }

    [Test]
    public async Task Query_FilterByConvertorColumn_CapturedVariable_Matches()
    {
        var id = Guid.NewGuid();
        await new TestConvertorItem { Id = id, Label = "captured", Value = 8 }
            .Insert().WithConnection(Connection).ExecuteAsync();

        string needle = "captured";
        var rows = await TestConvertorItem.Query(x => x.Label == needle)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows.Any(r => r.Id == id), Is.True);
    }

    [Test]
    public async Task Query_FilterByConvertorColumn_NotEqual_ExcludesConvertedValue()
    {
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        await new TestConvertorItem { Id = keep, Label = "alpha", Value = 1 }
            .Insert().WithConnection(Connection).ExecuteAsync();
        await new TestConvertorItem { Id = drop, Label = "beta", Value = 2 }
            .Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await TestConvertorItem.Query(x => x.Label != "beta")
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows.Any(r => r.Id == keep), Is.True, "non-matching row should be returned");
        Assert.That(rows.Any(r => r.Id == drop), Is.False, "the converted-equal row must be excluded");
    }

    [Test]
    public async Task Query_FilterByConvertorColumn_RepeatedCalls_StayCorrect()
    {
        var id = Guid.NewGuid();
        await new TestConvertorItem { Id = id, Label = "repeat", Value = 9 }
            .Insert().WithConnection(Connection).ExecuteAsync();

        // A convertor predicate is intentionally not cached; running it twice must each time re-translate and
        // convert the value, so both calls match. (Guards the cache-replay path from binding the raw value.)
        for (int i = 0; i < 2; i++)
        {
            var rows = await TestConvertorItem.Query(x => x.Label == "repeat")
                .WithConnection(Connection)
                .ExecuteAsync()
                .ToListAsync();
            Assert.That(rows.Any(r => r.Id == id), Is.True, $"iteration {i} must still match");
        }
    }

    [Test]
    public async Task Query_FilterByConvertorColumn_NonMatchingRawValue_ReturnsNothing()
    {
        var id = Guid.NewGuid();
        await new TestConvertorItem { Id = id, Label = "present", Value = 10 }
            .Insert().WithConnection(Connection).ExecuteAsync();

        // "ABSENT" converts to "ABSENT"; no such row exists.
        var rows = await TestConvertorItem.Query(x => x.Label == "absent" && x.Id == id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Is.Empty);
    }
}
