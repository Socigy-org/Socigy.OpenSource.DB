using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Socigy.OpenSource.DB.Core.CommandBuilders;
using UnitTest.DB;
using Bulk = Socigy.OpenSource.DB.Core.Bulk;

namespace UnitTest.DB.Tests;

/// <summary>
/// A <c>[Default]</c> column left unset must use the <b>server</b> default when the caller asks the server to
/// fill DB-defaulted columns (<see cref="InsertFields.ServerDefaults"/>), on every insert path (not just the
/// fluent <c>Insert().ExcludeAutoFields()</c>). The <c>keep</c> selector lets the caller still write some
/// <c>[Default]</c> columns by hand (e.g. a manual id) while the server fills the rest.
/// <c>test_items.created_at</c> is <c>[Default(DbDefaults.Time.Now)]</c> / <c>DEFAULT NOW()</c>;
/// <c>test_items.id</c> is <c>[Default(DbDefaults.Guid.Random)]</c>.
/// </summary>
[TestFixture]
public class InsertDefaultsTests : BaseUnitTest
{
    private async Task<TestItem> ReadByNameAsync(string name)
        => (await TestItem.Query(x => x.Name == name).WithConnection(Connection).ExecuteAsync().ToListAsync()).Single();

    [Test]
    public async Task Static_InsertMultiple_ServerDefaults_uses_server_now()
    {
        var name = $"def-multi-{Guid.NewGuid():N}";
        // Id and CreatedAt left at CLR default; both are [Default] columns.
        await TestItem.InsertMultipleAsync(new[] { new TestItem { Name = name, Priority = 5 } }, Connection, fields: InsertFields.ServerDefaults);

        var row = await ReadByNameAsync(name);
        Assert.That(row.Id, Is.Not.EqualTo(Guid.Empty), "id [Default] omitted -> server gen_random_uuid()");
        Assert.That(row.CreatedAt.Year, Is.GreaterThanOrEqualTo(2025), "created_at [Default] omitted -> server NOW(), not 0001-01-01");
        Assert.That(row.Priority, Is.EqualTo(5), "non-default columns are still written");
    }

    [Test]
    public async Task BulkCopy_ServerDefaults_uses_server_now()
    {
        var name = $"def-copy-{Guid.NewGuid():N}";
        await Bulk.BulkCopy.InsertMultipleCopyAsync(new[] { new TestItem { Name = name, Priority = 7 } }, Connection, fields: InsertFields.ServerDefaults);

        var row = await ReadByNameAsync(name);
        Assert.That(row.Id, Is.Not.EqualTo(Guid.Empty));
        Assert.That(row.CreatedAt.Year, Is.GreaterThanOrEqualTo(2025));
        Assert.That(row.Priority, Is.EqualTo(7));
    }

    [Test]
    public async Task Keep_writes_manual_id_while_server_fills_created_at()
    {
        var name = $"def-keep-{Guid.NewGuid():N}";
        var id = Guid.NewGuid();
        // keep Id (write our value), but let the server fill the other [Default] column (CreatedAt).
        await TestItem.InsertMultipleAsync(
            new[] { new TestItem { Id = id, Name = name, Priority = 9 } },
            Connection, fields: InsertFields.ServerDefaults, keep: x => new object?[] { x.Id });

        var row = await ReadByNameAsync(name);
        Assert.That(row.Id, Is.EqualTo(id), "kept [Default] id -> our value is written");
        Assert.That(row.CreatedAt.Year, Is.GreaterThanOrEqualTo(2025), "unkept [Default] created_at -> server NOW()");
    }

    [Test]
    public async Task Default_path_still_writes_the_CLR_value()
    {
        var name = $"def-clr-{Guid.NewGuid():N}";
        // With InsertFields.Default, the property's current value (CLR default DateTime) is written, not NOW().
        await TestItem.InsertMultipleAsync(new[] { new TestItem { Id = Guid.NewGuid(), Name = name, Priority = 1 } }, Connection);

        var row = await ReadByNameAsync(name);
        Assert.That(row.CreatedAt.Year, Is.EqualTo(1), "the CLR default DateTime (0001-01-01) was written, not the server default");
    }
}
