using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnitTest.DB;
using Bulk = Socigy.OpenSource.DB.Core.Bulk;

namespace UnitTest.DB.Tests;

/// <summary>
/// Issue #6: a <c>[Default]</c> column left unset must use the <b>server</b> default when the caller excludes
/// DB-defaulted columns, on every insert path (not just the fluent <c>Insert().ExcludeAutoFields()</c>).
/// <c>test_items.created_at</c> is <c>[Default(DbDefaults.Time.Now)]</c> / <c>DEFAULT NOW()</c>.
/// </summary>
[TestFixture]
public class InsertDefaultsTests : BaseUnitTest
{
    private async Task<TestItem> ReadByNameAsync(string name)
        => (await TestItem.Query(x => x.Name == name).WithConnection(Connection).ExecuteAsync().ToListAsync()).Single();

    [Test]
    public async Task Static_InsertMultiple_excludeDbDefaults_uses_server_now()
    {
        var name = $"def-multi-{Guid.NewGuid():N}";
        // Id and CreatedAt left at CLR default; both are [Default] columns.
        await TestItem.InsertMultipleAsync(new[] { new TestItem { Name = name, Priority = 5 } }, Connection, excludeDbDefaults: true);

        var row = await ReadByNameAsync(name);
        Assert.That(row.Id, Is.Not.EqualTo(Guid.Empty), "id [Default] omitted -> server gen_random_uuid()");
        Assert.That(row.CreatedAt.Year, Is.GreaterThanOrEqualTo(2025), "created_at [Default] omitted -> server NOW(), not 0001-01-01");
        Assert.That(row.Priority, Is.EqualTo(5), "non-default columns are still written");
    }

    [Test]
    public async Task BulkCopy_excludeDbDefaults_uses_server_now()
    {
        var name = $"def-copy-{Guid.NewGuid():N}";
        await Bulk.BulkCopy.InsertMultipleCopyAsync(new[] { new TestItem { Name = name, Priority = 7 } }, Connection, excludeDbDefaults: true);

        var row = await ReadByNameAsync(name);
        Assert.That(row.Id, Is.Not.EqualTo(Guid.Empty));
        Assert.That(row.CreatedAt.Year, Is.GreaterThanOrEqualTo(2025));
        Assert.That(row.Priority, Is.EqualTo(7));
    }

    [Test]
    public async Task Default_path_still_writes_the_CLR_value()
    {
        var name = $"def-clr-{Guid.NewGuid():N}";
        // Without excludeDbDefaults, the property's current value (CLR default DateTime) is written, not NOW().
        await TestItem.InsertMultipleAsync(new[] { new TestItem { Id = Guid.NewGuid(), Name = name, Priority = 1 } }, Connection);

        var row = await ReadByNameAsync(name);
        Assert.That(row.CreatedAt.Year, Is.EqualTo(1), "the CLR default DateTime (0001-01-01) was written, not the server default");
    }
}
