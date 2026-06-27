using UnitTest.DB;

namespace UnitTest.DB.Tests;

/// <summary>
/// A [Table] that inherits a property from a base class — or declares properties across multiple `partial`
/// declarations — must map ALL of them as columns. The generator previously read only the one declaration that
/// carried [Table], so inherited / other-partial properties were silently dropped (never inserted or read).
/// </summary>
[TestFixture]
public class InheritedColumnTests : BaseUnitTest
{
    [SetUp]
    public Task Clean() => ClearAsync("test_inherited");

    [Test]
    public async Task InheritedAndPartialColumns_RoundTrip()
    {
        var id = Guid.NewGuid();
        await new InheritedItem { Id = id, Name = "n", Score = 42, CreatedBy = "alice" }
            .Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await InheritedItem.Query(x => x.Id == id).WithConnection(Connection).ExecuteAsync().ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Name, Is.EqualTo("n"));
        Assert.That(rows[0].Score, Is.EqualTo(42), "a column declared in a second partial must round-trip");
        Assert.That(rows[0].CreatedBy, Is.EqualTo("alice"), "an inherited base-class column must round-trip");
    }

    // Filtering on an inherited column must also translate (it must be a real column, not dropped).
    [Test]
    public async Task FilterOnInheritedColumn_Works()
    {
        await new InheritedItem { Id = Guid.NewGuid(), Name = "a", CreatedBy = "bob" }
            .Insert().WithConnection(Connection).ExecuteAsync();
        await new InheritedItem { Id = Guid.NewGuid(), Name = "b", CreatedBy = "carol" }
            .Insert().WithConnection(Connection).ExecuteAsync();

        long count = await InheritedItem.Query(x => x.CreatedBy == "bob").WithConnection(Connection).CountAsync();
        Assert.That(count, Is.EqualTo(1));
    }
}
