using System.Data.Common;
using Socigy.OpenSource.DB.Core;
using Socigy.OpenSource.DB.Core.Context;
using Socigy.OpenSource.DB.TestDb.Context;

namespace UnitTest.DB.Tests;

/// <summary>
/// Live-PostgreSQL tests for the generated context layer: unit-of-work scopes, transactions
/// (commit/rollback), streaming, the no-MARS busy guard, and type binding via <see cref="TestTypes"/>.
/// </summary>
[TestFixture]
public class ContextIntegrationTests : BaseUnitTest
{
    /// <summary>Minimal connection factory that hands the context a fresh test connection.</summary>
    private sealed class TestConnectionFactory : IDbConnectionFactory
    {
        public DbConnection Create(string? connectionKey = null) => UnitCore.CreateConnection();
        public Task<bool> EnsureDbExists() => Task.FromResult(true);
    }

    private static TestDbFactory NewFactory(ConnectionLifetime lifetime = ConnectionLifetime.PerScope)
        => new(new TestConnectionFactory(), new SocigyDbContextOptions { ConnectionLifetime = lifetime });

    [SetUp]
    public async Task CleanContextTables()
    {
        await ClearAsync("test_items");
        await ClearAsync("test_types");
    }

    [Test]
    public async Task ExecuteAsync_InsertThenToList_RoundTrips()
    {
        var factory = NewFactory();
        var id = Guid.NewGuid();

        await factory.ExecuteAsync(async db =>
        {
            await db.TestItems.InsertAsync(new TestItem { Id = id, Name = "ctx" });
        });

        var rows = await factory.ExecuteAsync(db => db.TestItems.ToListAsync(x => x.Name == "ctx"));

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Id, Is.EqualTo(id));
    }

    [Test]
    public async Task ExecuteTransactionAsync_Commits_OnSuccess()
    {
        var factory = NewFactory();

        await factory.ExecuteTransactionAsync(async db =>
        {
            await db.TestItems.InsertAsync(new TestItem { Id = Guid.NewGuid(), Name = "committed" });
            await db.TestItems.InsertAsync(new TestItem { Id = Guid.NewGuid(), Name = "committed" });
        });

        Assert.That(await CountAsync("test_items"), Is.EqualTo(2));
    }

    [Test]
    public void ExecuteTransactionAsync_RollsBack_OnException()
    {
        var factory = NewFactory();

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await factory.ExecuteTransactionAsync(async db =>
            {
                await db.TestItems.InsertAsync(new TestItem { Id = Guid.NewGuid(), Name = "rollback" });
                throw new InvalidOperationException("boom");
            }));

        Assert.That(CountAsync("test_items").GetAwaiter().GetResult(), Is.EqualTo(0));
    }

    [Test]
    public async Task ForEachAsync_StreamsRows()
    {
        var factory = NewFactory();
        await factory.ExecuteAsync(async db =>
        {
            await db.TestItems.InsertAsync(new TestItem { Id = Guid.NewGuid(), Name = "a" });
            await db.TestItems.InsertAsync(new TestItem { Id = Guid.NewGuid(), Name = "b" });
        });

        var seen = new List<string>();
        await factory.ExecuteAsync(db => db.TestItems.ForEachAsync(null, row =>
        {
            seen.Add(row.Name);
            return Task.CompletedTask;
        }));

        Assert.That(seen, Has.Count.EqualTo(2));
    }

    [Test]
    public void BusyGuard_CommandDuringStream_ThrowsClearError()
    {
        var factory = NewFactory();

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await factory.ExecuteTransactionAsync(async db =>
            {
                await db.TestItems.InsertAsync(new TestItem { Id = Guid.NewGuid(), Name = "seed" });
                await db.TestItems.ForEachAsync(null, async row =>
                {
                    // Issuing another command on the same connection while streaming must fail fast.
                    await db.TestItems.InsertAsync(new TestItem { Id = Guid.NewGuid(), Name = "nested" });
                });
            }));
    }

    [Test]
    public async Task TypeBinding_RoundTripsBoolDecimalAndNull()
    {
        var factory = NewFactory();
        var id = Guid.NewGuid();
        await factory.ExecuteAsync(async db =>
        {
            await db.TestTypes.InsertAsync(new TestType
            {
                Id = id,
                IsActive = true,
                NullableValue = null,
                Amount = 12.5m,
                When = DateTime.Now,
                Note = null
            });
        });

        var back = await factory.ExecuteAsync(db => db.TestTypes.FirstOrDefaultAsync(x => x.Id == id));

        Assert.That(back, Is.Not.Null);
        Assert.That(back!.IsActive, Is.True);
        Assert.That(back.Amount, Is.EqualTo(12.5m));
        Assert.That(back.NullableValue, Is.Null);
    }

    [Test]
    public async Task ParserOperators_BoolAndNullable_FilterCorrectly()
    {
        var factory = NewFactory();
        await factory.ExecuteAsync(async db =>
        {
            await db.TestTypes.InsertAsync(new TestType { Id = Guid.NewGuid(), IsActive = true, NullableValue = 5, Amount = 1m, When = DateTime.Now });
            await db.TestTypes.InsertAsync(new TestType { Id = Guid.NewGuid(), IsActive = false, NullableValue = null, Amount = 2m, When = DateTime.Now });
        });

        var actives = await factory.ExecuteAsync(db => db.TestTypes.ToListAsync(x => x.IsActive));
        Assert.That(actives, Has.Count.EqualTo(1));

        var withValue = await factory.ExecuteAsync(db => db.TestTypes.CountAsync(x => x.NullableValue.HasValue));
        Assert.That(withValue, Is.EqualTo(1));
    }
}
