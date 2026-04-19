using UnitTest.DB;

namespace UnitTest.DB.Tests;

[TestFixture]
public class SequenceTests : BaseUnitTest
{
    [SetUp]
    public async Task Clean() => await ClearAsync("test_counters");

    // ------------------------------------------------------------------
    // GetNextValueAsync
    // ------------------------------------------------------------------

    [Test]
    public async Task GetNextValueAsync_ReturnsPositiveInteger()
    {
        long next = await TestCounter.SeqSequence.GetNextValueAsync(Connection);
        Assert.That(next, Is.GreaterThan(0));
    }

    [Test]
    public async Task GetNextValueAsync_IncrementsOnEachCall()
    {
        long first  = await TestCounter.SeqSequence.GetNextValueAsync(Connection);
        long second = await TestCounter.SeqSequence.GetNextValueAsync(Connection);

        Assert.That(second, Is.EqualTo(first + 1));
    }

    // ------------------------------------------------------------------
    // PeekCurrentValueAsync
    // ------------------------------------------------------------------

    [Test]
    public async Task PeekCurrentValueAsync_MatchesLastAdvanced()
    {
        long advanced = await TestCounter.SeqSequence.GetNextValueAsync(Connection);
        long peeked   = await TestCounter.SeqSequence.PeekCurrentValueAsync(Connection);

        Assert.That(peeked, Is.EqualTo(advanced));
    }

    // ------------------------------------------------------------------
    // AutoIncrement value populated in DB after INSERT
    // ------------------------------------------------------------------

    [Test]
    public async Task Insert_AutoIncrement_SeqAssignedByDb()
    {
        // Pre-advance so we know the next value
        long nextExpected = await TestCounter.SeqSequence.GetNextValueAsync(Connection);

        var counter = new TestCounter { Id = Guid.NewGuid(), Label = "SeqTest" };
        // Seq should be omitted from INSERT (auto-increment) and assigned by DB
        bool ok = await counter.Insert()
            .WithConnection(Connection)
            .ExecuteAsync();

        Assert.That(ok, Is.True);

        var rows = await TestCounter.Query(x => x.Id == counter.Id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Seq, Is.EqualTo((int)(nextExpected + 1)),
            "Seq should equal the next sequence value after the pre-advance");
    }

    [Test]
    public async Task Insert_TwoRows_SeqValuesIncrement()
    {
        var a = new TestCounter { Id = Guid.NewGuid(), Label = "A" };
        var b = new TestCounter { Id = Guid.NewGuid(), Label = "B" };

        await a.Insert().WithConnection(Connection).ExecuteAsync();
        await b.Insert().WithConnection(Connection).ExecuteAsync();

        var rows = await TestCounter.Query()
            .WithConnection(Connection)
            .OrderBy(x => new object[] { x.Seq })
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows[1].Seq, Is.EqualTo(rows[0].Seq + 1));
    }

    // ------------------------------------------------------------------
    // WithValuePropagation reads back the DB-assigned Seq
    // ------------------------------------------------------------------

    [Test]
    public async Task Insert_WithValuePropagation_SeqPopulatedOnInstance()
    {
        var counter = new TestCounter { Id = Guid.NewGuid(), Label = "VP" };
        Assert.That(counter.Seq, Is.EqualTo(0), "precondition: Seq starts at 0");

        await counter.Insert()
            .WithConnection(Connection)
            .WithValuePropagation()
            .ExecuteAsync();

        Assert.That(counter.Seq, Is.GreaterThan(0), "Seq should be populated by RETURNING *");
    }

    // ------------------------------------------------------------------
    // WithAllFields lets caller supply an explicit sequence value
    // ------------------------------------------------------------------

    [Test]
    public async Task Insert_WithAllFields_ExplicitSeqInserted()
    {
        int explicitSeq = 9999;
        var counter = new TestCounter { Id = Guid.NewGuid(), Label = "Explicit", Seq = explicitSeq };

        bool ok = await counter.Insert()
            .WithConnection(Connection)
            .WithAllFields()
            .ExecuteAsync();

        Assert.That(ok, Is.True);

        var rows = await TestCounter.Query(x => x.Id == counter.Id)
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(rows[0].Seq, Is.EqualTo(explicitSeq));
    }
}
