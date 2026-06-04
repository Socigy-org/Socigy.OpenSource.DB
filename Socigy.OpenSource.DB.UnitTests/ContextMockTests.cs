using System;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Socigy.OpenSource.DB.Core.Context;
using Socigy.OpenSource.DB.TestDb.Context;
using UnitTest.DB;

namespace Socigy.OpenSource.DB.UnitTests
{
    /// <summary>
    /// Proves the generated context is a clean, mockable seam: a service depending on
    /// <see cref="ISocigyDatabaseFactory{TDatabase}"/> can be unit-tested with no database by mocking the
    /// factory, the context and the table set.
    /// </summary>
    [TestFixture]
    public class ContextMockTests
    {
        private sealed class ItemService
        {
            private readonly ISocigyDatabaseFactory<ITestDb> _db;
            public ItemService(ISocigyDatabaseFactory<ITestDb> db) => _db = db;

            public Task<bool> CreateAsync(string name) =>
                _db.ExecuteTransactionAsync(async db =>
                {
                    var item = new TestItem { Id = Guid.NewGuid(), Name = name };
                    return await db.TestItems.InsertAsync(item);
                });
        }

        [Test]
        public async Task Service_IsUnitTestable_WithMocks_NoDatabase()
        {
            var items = new Mock<ITestItemSet>();
            items.Setup(s => s.InsertAsync(It.IsAny<TestItem>())).ReturnsAsync(true);

            var ctx = new Mock<ITestDb>();
            ctx.SetupGet(c => c.TestItems).Returns(items.Object);

            var factory = new Mock<ISocigyDatabaseFactory<ITestDb>>();
            factory
                .Setup(f => f.ExecuteTransactionAsync(It.IsAny<Func<ITestDb, Task<bool>>>(), It.IsAny<CancellationToken>()))
                .Returns((Func<ITestDb, Task<bool>> work, CancellationToken _) => work(ctx.Object));

            var sut = new ItemService(factory.Object);
            bool ok = await sut.CreateAsync("alpha");

            Assert.That(ok, Is.True);
            items.Verify(s => s.InsertAsync(It.Is<TestItem>(i => i.Name == "alpha")), Times.Once);
        }
    }
}
