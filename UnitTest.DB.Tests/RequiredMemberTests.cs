using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnitTest.DB;

namespace UnitTest.DB.Tests;

/// <summary>
/// Issue #2: a <c>[Table]</c> entity with a <c>required</c> member must work. The fact that <c>UnitTest.DB</c>
/// compiles with <see cref="RequiredItem"/> already proves the generated code carries <c>[SetsRequiredMembers]</c>
/// (its materializer/builders use <c>new()</c>); this round-trips it against the database too.
/// </summary>
[TestFixture]
public class RequiredMemberTests : BaseUnitTest
{
    [Test]
    public async Task Required_member_entity_round_trips()
    {
        var id = Guid.NewGuid();
        var label = $"req-{Guid.NewGuid():N}";
        await RequiredItem.InsertMultipleAsync(new[] { new RequiredItem { Id = id, Label = label } }, Connection);

        var rows = await RequiredItem.Query(x => x.Label == label).WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Label, Is.EqualTo(label));
        Assert.That(rows[0].Id, Is.EqualTo(id));
    }
}
