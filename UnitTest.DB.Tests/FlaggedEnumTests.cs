using UnitTest.DB;

namespace UnitTest.DB.Tests;

/// <summary>
/// Tests for the N:M junction-table methods generated for <c>[FlaggedEnum]</c> properties,
/// and for the <c>HasFlag</c> translation in WHERE clauses.
/// </summary>
[TestFixture]
public class FlaggedEnumTests : BaseUnitTest
{
    private TestUser _alice = null!;
    private TestUser _bob   = null!;

    [SetUp]
    public async Task Seed()
    {
        await ClearAsync("test_users_test_roles");
        await ClearAsync("test_users");

        // Ensure all role reference rows exist (idempotent upsert).
        await using var roleCmd = Connection.CreateCommand();
        roleCmd.CommandText = @"
            INSERT INTO ""test_roles"" (""id"", ""name"") VALUES
                (1, 'Reader'), (2, 'Writer'), (3, 'Moderator'), (4, 'Admin')
            ON CONFLICT (""id"") DO UPDATE SET ""name"" = EXCLUDED.""name""";
        await roleCmd.ExecuteNonQueryAsync();

        _alice = new TestUser { Id = Guid.NewGuid(), Username = "alice" };
        _bob   = new TestUser { Id = Guid.NewGuid(), Username = "bob"   };

        await _alice.Insert().WithConnection(Connection).ExecuteAsync();
        await _bob  .Insert().WithConnection(Connection).ExecuteAsync();
    }

    // ------------------------------------------------------------------
    // InsertRoleAsync
    // ------------------------------------------------------------------

    [Test]
    public async Task InsertRoleAsync_AddsEntryInJunctionTable()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Reader, Connection);

        long count = await CountAsync("test_users_test_roles");
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task InsertRoleAsync_MultipleRoles_AllPresent()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Reader,    Connection);
        await TestUser.InsertRoleAsync(_alice, TestRole.Writer,    Connection);
        await TestUser.InsertRoleAsync(_alice, TestRole.Moderator, Connection);

        long count = await CountAsync("test_users_test_roles");
        Assert.That(count, Is.EqualTo(3));
    }

    // ------------------------------------------------------------------
    // HasRoleFlagAsync
    // ------------------------------------------------------------------

    [Test]
    public async Task HasRoleFlagAsync_PresentRole_ReturnsTrue()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Admin, Connection);

        bool has = await TestUser.HasRoleFlagAsync(_alice, TestRole.Admin, Connection);
        Assert.That(has, Is.True);
    }

    [Test]
    public async Task HasRoleFlagAsync_AbsentRole_ReturnsFalse()
    {
        bool has = await TestUser.HasRoleFlagAsync(_alice, TestRole.Admin, Connection);
        Assert.That(has, Is.False);
    }

    [Test]
    public async Task HasRoleFlagAsync_OtherUserSameRole_Isolated()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Admin, Connection);

        bool bobHas = await TestUser.HasRoleFlagAsync(_bob, TestRole.Admin, Connection);
        Assert.That(bobHas, Is.False, "Bob should not have Alice's role");
    }

    // ------------------------------------------------------------------
    // GetRolesAsync
    // ------------------------------------------------------------------

    [Test]
    public async Task GetRolesAsync_NoRoles_ReturnsEmpty()
    {
        var roles = await TestUser.GetRolesAsync(_alice, Connection).ToListAsync();
        Assert.That(roles, Is.Empty);
    }

    [Test]
    public async Task GetRolesAsync_TwoRoles_ReturnsBoth()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Reader, Connection);
        await TestUser.InsertRoleAsync(_alice, TestRole.Writer, Connection);

        var roles = await TestUser.GetRolesAsync(_alice, Connection).ToListAsync();

        Assert.That(roles, Has.Count.EqualTo(2));
        Assert.That(roles, Does.Contain(TestRole.Reader));
        Assert.That(roles, Does.Contain(TestRole.Writer));
    }

    // ------------------------------------------------------------------
    // DeleteRoleAsync
    // ------------------------------------------------------------------

    [Test]
    public async Task DeleteRoleAsync_RemovesSpecifiedRole()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Reader, Connection);
        await TestUser.InsertRoleAsync(_alice, TestRole.Writer, Connection);

        await TestUser.DeleteRoleAsync(_alice, TestRole.Reader, Connection);

        var roles = await TestUser.GetRolesAsync(_alice, Connection).ToListAsync();
        Assert.That(roles, Has.Count.EqualTo(1));
        Assert.That(roles[0], Is.EqualTo(TestRole.Writer));
    }

    [Test]
    public async Task DeleteRoleAsync_NonExistentRole_NoError()
    {
        Assert.DoesNotThrowAsync(async () =>
            await TestUser.DeleteRoleAsync(_alice, TestRole.Admin, Connection));
    }

    // ------------------------------------------------------------------
    // SyncRolesAsync
    // ------------------------------------------------------------------

    [Test]
    public async Task SyncRolesAsync_EmptyDesired_RemovesAll()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Reader, Connection);
        await TestUser.InsertRoleAsync(_alice, TestRole.Writer, Connection);

        await TestUser.SyncRolesAsync(_alice, [], Connection);

        var roles = await TestUser.GetRolesAsync(_alice, Connection).ToListAsync();
        Assert.That(roles, Is.Empty);
    }

    [Test]
    public async Task SyncRolesAsync_AddsMissing_RemovesStale()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Reader,    Connection);
        await TestUser.InsertRoleAsync(_alice, TestRole.Moderator, Connection);

        // Desired: Writer + Admin  (Reader and Moderator should be removed)
        await TestUser.SyncRolesAsync(_alice, [TestRole.Writer, TestRole.Admin], Connection);

        var roles = await TestUser.GetRolesAsync(_alice, Connection).ToListAsync();

        Assert.That(roles, Has.Count.EqualTo(2));
        Assert.That(roles, Does.Contain(TestRole.Writer));
        Assert.That(roles, Does.Contain(TestRole.Admin));
        Assert.That(roles, Does.Not.Contain(TestRole.Reader));
        Assert.That(roles, Does.Not.Contain(TestRole.Moderator));
    }

    [Test]
    public async Task SyncRolesAsync_NoChanges_DoesNothing()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Admin, Connection);

        await TestUser.SyncRolesAsync(_alice, [TestRole.Admin], Connection);


        long count = await CountAsync("test_users_test_roles");
        Assert.That(count, Is.EqualTo(1));
    }

    // ------------------------------------------------------------------
    // HasFlag in WHERE clause → EXISTS subquery
    // ------------------------------------------------------------------

    [Test]
    public async Task Query_HasFlagWhere_MatchesUsersWithRole()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Admin,  Connection);
        await TestUser.InsertRoleAsync(_bob,   TestRole.Reader, Connection);

        var admins = await TestUser.Query(x => x.Role.HasFlag(TestRole.Admin))
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(admins, Has.Count.EqualTo(1));
        Assert.That(admins[0].Username, Is.EqualTo("alice"));
    }

    [Test]
    public async Task Query_HasFlagWhere_NoMatches_ReturnsEmpty()
    {
        // No one has Moderator
        var mods = await TestUser.Query(x => x.Role.HasFlag(TestRole.Moderator))
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(mods, Is.Empty);
    }

    [Test]
    public async Task Query_HasFlagWhere_CombinedWithOtherPredicate()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Admin, Connection);
        await TestUser.InsertRoleAsync(_bob,   TestRole.Admin, Connection);

        var results = await TestUser.Query(x => x.Role.HasFlag(TestRole.Admin) && x.Username == "alice")
            .WithConnection(Connection)
            .ExecuteAsync()
            .ToListAsync();

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Username, Is.EqualTo("alice"));
    }

    // ------------------------------------------------------------------
    // EditRoleCommandBuilder (fluent batch builder)
    // ------------------------------------------------------------------

    [Test]
    public async Task EditRole_AddFlags_InsertsRoles()
    {
        await _alice.EditRole()
            .WithConnection(Connection)
            .AddFlags(TestRole.Reader, TestRole.Writer)
            .ExecuteAsync();

        var roles = await TestUser.GetRolesAsync(_alice, Connection).ToListAsync();
        Assert.That(roles, Has.Count.EqualTo(2));
        Assert.That(roles, Does.Contain(TestRole.Reader));
        Assert.That(roles, Does.Contain(TestRole.Writer));
    }

    [Test]
    public async Task EditRole_RemoveFlags_DeletesRoles()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Reader,    Connection);
        await TestUser.InsertRoleAsync(_alice, TestRole.Writer,    Connection);
        await TestUser.InsertRoleAsync(_alice, TestRole.Moderator, Connection);

        await _alice.EditRole()
            .WithConnection(Connection)
            .RemoveFlags(TestRole.Reader, TestRole.Moderator)
            .ExecuteAsync();

        var roles = await TestUser.GetRolesAsync(_alice, Connection).ToListAsync();
        Assert.That(roles, Has.Count.EqualTo(1));
        Assert.That(roles[0], Is.EqualTo(TestRole.Writer));
    }

    [Test]
    public async Task EditRole_AddAndRemove_AppliesBothInOrder()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Admin, Connection);

        await _alice.EditRole()
            .WithConnection(Connection)
            .AddFlags(TestRole.Reader)
            .RemoveFlags(TestRole.Admin)
            .ExecuteAsync();

        var roles = await TestUser.GetRolesAsync(_alice, Connection).ToListAsync();
        Assert.That(roles, Has.Count.EqualTo(1));
        Assert.That(roles[0], Is.EqualTo(TestRole.Reader));
    }

    // ------------------------------------------------------------------
    // Instance wrappers
    // ------------------------------------------------------------------

    [Test]
    public async Task InstanceWrapper_InsertRoleAsync_Works()
    {
        await _alice.InsertRoleAsync(TestRole.Writer, Connection);

        bool has = await TestUser.HasRoleFlagAsync(_alice, TestRole.Writer, Connection);
        Assert.That(has, Is.True);
    }

    [Test]
    public async Task InstanceWrapper_DeleteRoleAsync_Works()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Admin, Connection);
        await _alice.DeleteRoleAsync(TestRole.Admin, Connection);

        bool has = await TestUser.HasRoleFlagAsync(_alice, TestRole.Admin, Connection);
        Assert.That(has, Is.False);
    }

    [Test]
    public async Task InstanceWrapper_GetRolesAsync_Works()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Moderator, Connection);

        var roles = await _alice.GetRolesAsync(Connection).ToListAsync();
        Assert.That(roles, Does.Contain(TestRole.Moderator));
    }

    [Test]
    public async Task InstanceWrapper_HasRoleFlagAsync_Works()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Admin, Connection);

        bool has = await _alice.HasRoleFlagAsync(TestRole.Admin, Connection);
        Assert.That(has, Is.True);
    }

    // ------------------------------------------------------------------
    // In-memory cache: AddRoleFlag / RemoveRoleFlag / LoadRolesAsync / CommitRolesAsync
    // ------------------------------------------------------------------

    [Test]
    public async Task InMemoryCache_AddAndCommit_PersistsToDb()
    {
        _alice.AddRoleFlag(TestRole.Reader);
        _alice.AddRoleFlag(TestRole.Writer);
        await _alice.CommitRolesAsync(Connection);

        var roles = await TestUser.GetRolesAsync(_alice, Connection).ToListAsync();
        Assert.That(roles, Has.Count.EqualTo(2));
        Assert.That(roles, Does.Contain(TestRole.Reader));
        Assert.That(roles, Does.Contain(TestRole.Writer));
    }

    [Test]
    public async Task InMemoryCache_LoadThenRemoveThenCommit_RemovesFromDb()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Admin,   Connection);
        await TestUser.InsertRoleAsync(_alice, TestRole.Reader,  Connection);

        await _alice.LoadRolesAsync(Connection);
        _alice.RemoveRoleFlag(TestRole.Admin);
        await _alice.CommitRolesAsync(Connection);

        var roles = await TestUser.GetRolesAsync(_alice, Connection).ToListAsync();
        Assert.That(roles, Has.Count.EqualTo(1));
        Assert.That(roles[0], Is.EqualTo(TestRole.Reader));
    }

    [Test]
    public async Task InMemoryCache_CommitWithNullCache_DoesNothing()
    {
        await TestUser.InsertRoleAsync(_alice, TestRole.Admin, Connection);

        // No LoadRolesAsync called — cache is null
        Assert.DoesNotThrowAsync(async () => await _alice.CommitRolesAsync(Connection));

        long count = await CountAsync("test_users_test_roles");
        Assert.That(count, Is.EqualTo(1), "CommitRolesAsync with null cache should be a no-op");
    }
}
