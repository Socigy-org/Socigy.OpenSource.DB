using System.Text.Json;
using UnitTest.DB;

namespace UnitTest.DB.Tests;

[TestFixture]
public class JsonColumnTests : BaseUnitTest
{
    [SetUp]
    public async Task Clean() => await ClearAsync("test_json_items");

    // ------------------------------------------------------------------
    // Raw JSON column — insert and read back
    // ------------------------------------------------------------------

    [Test]
    public async Task Insert_RawJsonColumn_StoredAndRetrievedCorrectly()
    {
        var rawJson = """{"key":"value","num":42}""";
        var item = new TestJsonItem { Id = Guid.NewGuid(), Name = "RawTest", RawData = rawJson };

        bool ok = await item.Insert().WithConnection(Connection).ExecuteAsync();
        Assert.That(ok, Is.True);

        var rows = await TestJsonItem.Query(x => x.Id == item.Id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        var fetched = rows[0];
        Assert.That(fetched.RawData, Is.Not.Null);

        // PostgreSQL normalises JSONB whitespace — compare via parse
        using var doc = JsonDocument.Parse(fetched.RawData!);
        Assert.That(doc.RootElement.GetProperty("key").GetString(), Is.EqualTo("value"));
        Assert.That(doc.RootElement.GetProperty("num").GetInt32(), Is.EqualTo(42));
    }

    [Test]
    public async Task Insert_NullRawJsonColumn_RetrievedAsNull()
    {
        var item = new TestJsonItem { Id = Guid.NewGuid(), Name = "NullRaw", RawData = null };
        await item.Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await TestJsonItem.Query(x => x.Id == item.Id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows[0].RawData, Is.Null);
    }

    [Test]
    public async Task Insert_RawJsonColumn_ArrayPayload_RoundTrips()
    {
        var rawJson = """[1,2,3]""";
        var item = new TestJsonItem { Id = Guid.NewGuid(), Name = "ArrayRaw", RawData = rawJson };
        await item.Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await TestJsonItem.Query(x => x.Id == item.Id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        using var doc = JsonDocument.Parse(rows[0].RawData!);
        Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(doc.RootElement.GetArrayLength(), Is.EqualTo(3));
    }

    // ------------------------------------------------------------------
    // Typed JSON column — insert and read back
    // ------------------------------------------------------------------

    [Test]
    public async Task Insert_TypedJsonColumn_RoundTripsCorrectly()
    {
        var payload = new TestJsonPayload
        {
            Title = "Hello JSON",
            Score = 99,
            Tags = ["unit", "test", "json"]
        };
        var item = new TestJsonItem { Id = Guid.NewGuid(), Name = "TypedTest", Payload = payload };

        bool ok = await item.Insert().WithConnection(Connection).ExecuteAsync();
        Assert.That(ok, Is.True);

        var rows = await TestJsonItem.Query(x => x.Id == item.Id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        var fetched = rows[0];
        Assert.That(fetched.Payload, Is.Not.Null);
        Assert.That(fetched.Payload!.Title, Is.EqualTo("Hello JSON"));
        Assert.That(fetched.Payload.Score, Is.EqualTo(99));
        Assert.That(fetched.Payload.Tags, Is.EquivalentTo(new[] { "unit", "test", "json" }));
    }

    [Test]
    public async Task Insert_NullTypedJsonColumn_RetrievedAsNull()
    {
        var item = new TestJsonItem { Id = Guid.NewGuid(), Name = "NullTyped", Payload = null };
        await item.Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await TestJsonItem.Query(x => x.Id == item.Id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows[0].Payload, Is.Null);
    }

    [Test]
    public async Task Insert_TypedJsonColumn_EmptyTags_RoundTrips()
    {
        var payload = new TestJsonPayload { Title = "No Tags", Score = 0, Tags = [] };
        var item = new TestJsonItem { Id = Guid.NewGuid(), Name = "EmptyTags", Payload = payload };
        await item.Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await TestJsonItem.Query(x => x.Id == item.Id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows[0].Payload!.Tags, Is.Empty);
    }

    // ------------------------------------------------------------------
    // Both columns set simultaneously
    // ------------------------------------------------------------------

    [Test]
    public async Task Insert_BothJsonColumns_BothRoundTrip()
    {
        var raw = """{"flag":true}""";
        var payload = new TestJsonPayload { Title = "Both", Score = 7, Tags = ["a", "b"] };
        var item = new TestJsonItem
        {
            Id = Guid.NewGuid(),
            Name = "BothSet",
            RawData = raw,
            Payload = payload
        };
        await item.Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await TestJsonItem.Query(x => x.Id == item.Id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        var fetched = rows[0];
        using var doc = JsonDocument.Parse(fetched.RawData!);
        Assert.That(doc.RootElement.GetProperty("flag").GetBoolean(), Is.True);
        Assert.That(fetched.Payload!.Title, Is.EqualTo("Both"));
        Assert.That(fetched.Payload.Tags, Has.Count.EqualTo(2));
    }

    // ------------------------------------------------------------------
    // Update — typed JSON column
    // ------------------------------------------------------------------

    [Test]
    public async Task Update_TypedJsonColumn_UpdatesCorrectly()
    {
        var item = new TestJsonItem
        {
            Id = Guid.NewGuid(),
            Name = "UpdateTyped",
            Payload = new TestJsonPayload { Title = "Before", Score = 1, Tags = [] }
        };
        await item.Insert().WithConnection(Connection).ExecuteAsync();

        item.Payload = new TestJsonPayload { Title = "After", Score = 2, Tags = ["updated"] };
        await item.Update().WithConnection(Connection).WithAllFields().ExecuteAsync();

        var rows = await TestJsonItem.Query(x => x.Id == item.Id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        var fetched = rows[0];
        Assert.That(fetched.Payload!.Title, Is.EqualTo("After"));
        Assert.That(fetched.Payload.Score, Is.EqualTo(2));
        Assert.That(fetched.Payload.Tags, Contains.Item("updated"));
    }

    [Test]
    public async Task Update_TypedJsonColumn_SetToNull_RetrievedAsNull()
    {
        var item = new TestJsonItem
        {
            Id = Guid.NewGuid(),
            Name = "NullUpdate",
            Payload = new TestJsonPayload { Title = "Will be nulled", Score = 5, Tags = [] }
        };
        await item.Insert().WithConnection(Connection).ExecuteAsync();

        item.Payload = null;
        await item.Update().WithConnection(Connection).WithAllFields().ExecuteAsync();

        var rows = await TestJsonItem.Query(x => x.Id == item.Id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows[0].Payload, Is.Null);
    }

    // ------------------------------------------------------------------
    // Update — raw JSON column
    // ------------------------------------------------------------------

    [Test]
    public async Task Update_RawJsonColumn_UpdatesCorrectly()
    {
        var item = new TestJsonItem { Id = Guid.NewGuid(), Name = "UpdateRaw", RawData = """{"v":1}""" };
        await item.Insert().WithConnection(Connection).ExecuteAsync();

        item.RawData = """{"v":2}""";
        await item.Update().WithConnection(Connection).WithAllFields().ExecuteAsync();

        var rows = await TestJsonItem.Query(x => x.Id == item.Id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        using var doc = JsonDocument.Parse(rows[0].RawData!);
        Assert.That(doc.RootElement.GetProperty("v").GetInt32(), Is.EqualTo(2));
    }

    // ------------------------------------------------------------------
    // Multiple rows — query all
    // ------------------------------------------------------------------

    [Test]
    public async Task Query_MultipleJsonRows_AllReturned()
    {
        for (int i = 0; i < 3; i++)
        {
            var item = new TestJsonItem
            {
                Id = Guid.NewGuid(),
                Name = $"Item{i}",
                Payload = new TestJsonPayload { Title = $"Payload{i}", Score = i, Tags = [$"tag{i}"] }
            };
            await item.Insert().WithConnection(Connection).ExecuteAsync();
        }

        var rows = await TestJsonItem.Query()
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(3));
        Assert.That(rows.Select(r => r.Payload!.Score).Order(), Is.EqualTo(new[] { 0, 1, 2 }));
    }

    // ------------------------------------------------------------------
    // ColumnInfo metadata — IsJson flag on generated code
    // ------------------------------------------------------------------

    [Test]
    public void GetColumns_JsonColumns_HaveIsJsonTrue()
    {
        var columns = new TestJsonItem().GetColumns();

        Assert.That(columns[TestJsonItem.RawDataColumnName].IsJson, Is.True,
            "RawData ([RawJsonColumn]) must report IsJson = true");
        Assert.That(columns[TestJsonItem.PayloadColumnName].IsJson, Is.True,
            "Payload ([JsonColumn]) must report IsJson = true");
    }

    [Test]
    public void GetColumns_NonJsonColumns_HaveIsJsonFalse()
    {
        var columns = new TestJsonItem().GetColumns();

        Assert.That(columns[TestJsonItem.IdColumnName].IsJson, Is.False);
        Assert.That(columns[TestJsonItem.NameColumnName].IsJson, Is.False);
    }

    [Test]
    public void GetColumns_JsonColumns_TypeReportedAsString()
    {
        var columns = new TestJsonItem().GetColumns();

        // Both raw and typed JSON are stored as text in ColumnInfo (the DB value is a JSON string)
        Assert.That(columns[TestJsonItem.RawDataColumnName].Type, Is.EqualTo(typeof(string)));
        Assert.That(columns[TestJsonItem.PayloadColumnName].Type, Is.EqualTo(typeof(string)));
    }
}
