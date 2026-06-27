using UnitTest.DB;

namespace UnitTest.DB.Tests;

/// <summary>
/// A <c>char</c> column is stored as <c>character(1)</c>. Npgsql cannot bind or read a bare <see cref="System.Char"/>,
/// so the write paths must rebind it as a one-character string and the fast read path must narrow back. Before the fix
/// the parameterized insert/update threw (no NpgsqlDbType for System.Char) and the fast read path could not read it.
/// </summary>
[TestFixture]
public class CharColumnTests : BaseUnitTest
{
    [SetUp]
    public Task Clean() => ClearAsync("char_items");

    [Test]
    public async Task Insert_And_Query_CharColumn_RoundTrips()
    {
        var id = Guid.NewGuid();
        await new CharItem { Id = id, Grade = 'A', Initial = 'Z' }
            .Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await CharItem.Query(x => x.Id == id).WithConnection(Connection).ExecuteAsync().ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1), "the row must materialize (a char column previously threw on read)");
        Assert.That(rows[0].Grade, Is.EqualTo('A'));
        Assert.That(rows[0].Initial, Is.EqualTo((char?)'Z'));
    }

    [Test]
    public async Task Update_CharColumn_Persists()
    {
        var id = Guid.NewGuid();
        await new CharItem { Id = id, Grade = 'A' }
            .Insert().WithConnection(Connection).ExecuteAsync();

        var item = new CharItem { Id = id, Grade = 'B' };
        await item.Update().WithConnection(Connection).WithFields(x => new object[] { x.Grade }).ExecuteAsync();

        var rows = await CharItem.Query(x => x.Id == id).WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(rows[0].Grade, Is.EqualTo('B'));
    }

    [Test]
    public async Task NullableChar_NullValue_RoundTrips()
    {
        var id = Guid.NewGuid();
        await new CharItem { Id = id, Grade = 'C', Initial = null }
            .Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await CharItem.Query(x => x.Id == id).WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(rows[0].Grade, Is.EqualTo('C'));
        Assert.That(rows[0].Initial, Is.Null);
    }

    [Test]
    public async Task BinaryCopy_CharColumn_RoundTrips()
    {
        var id = Guid.NewGuid();
        await CharItem.InsertMultipleCopyAsync(new[] { new CharItem { Id = id, Grade = 'D', Initial = 'Q' } }, Connection);

        var rows = await CharItem.Query(x => x.Id == id).WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Grade, Is.EqualTo('D'), "binary COPY must store the char value, not a corrupted byte");
        Assert.That(rows[0].Initial, Is.EqualTo((char?)'Q'));
    }
}
