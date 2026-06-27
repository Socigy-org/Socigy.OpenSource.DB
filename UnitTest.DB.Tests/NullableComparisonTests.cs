using UnitTest.DB;

namespace UnitTest.DB.Tests;

/// <summary>
/// A `!=` filter over a nullable value-type column must match C# semantics (null != value is true), so NULL rows
/// are included — SQL `col <> @p` alone excludes them.
/// </summary>
[TestFixture]
public class NullableComparisonTests : BaseUnitTest
{
    [SetUp]
    public Task Clean() => ClearAsync("test_types");

    [Test]
    public async Task NotEqual_NullableColumn_IncludesNullRows()
    {
        await new TestType { Id = Guid.NewGuid(), NullableValue = 10 }.Insert().WithConnection(Connection).ExecuteAsync();
        await new TestType { Id = Guid.NewGuid(), NullableValue = null }.Insert().WithConnection(Connection).ExecuteAsync();
        await new TestType { Id = Guid.NewGuid(), NullableValue = 5 }.Insert().WithConnection(Connection).ExecuteAsync();

        long count = await TestType.Query(x => x.NullableValue != 5).WithConnection(Connection).CountAsync();

        Assert.That(count, Is.EqualTo(2), "10 and NULL both satisfy `!= 5` in C# semantics; only the 5 row is excluded");
    }

    [Test]
    public async Task Equal_NullableColumn_ExcludesNullRows()
    {
        await new TestType { Id = Guid.NewGuid(), NullableValue = 5 }.Insert().WithConnection(Connection).ExecuteAsync();
        await new TestType { Id = Guid.NewGuid(), NullableValue = null }.Insert().WithConnection(Connection).ExecuteAsync();

        long count = await TestType.Query(x => x.NullableValue == 5).WithConnection(Connection).CountAsync();

        Assert.That(count, Is.EqualTo(1), "only the 5 row matches `== 5` (null == 5 is false)");
    }
}
