using Npgsql;

namespace UnitTest.DB.Tests;

/// <summary>
/// Base class for all tests.  Provides an open <see cref="NpgsqlConnection"/>
/// for each test and guarantees the schema exists before any fixture runs.
/// </summary>
public abstract class BaseUnitTest
{
    protected NpgsqlConnection Connection { get; private set; } = null!;

    [OneTimeSetUp]
    public static async Task InitializeOnce() => await UnitCore.InitializeAsync();

    [SetUp]
    public async Task OpenConnection()
    {
        Connection = UnitCore.CreateConnection();
        await Connection.OpenAsync();
    }

    [TearDown]
    public async Task CloseConnection()
    {
        await Connection.CloseAsync();
        await Connection.DisposeAsync();
    }

    /// <summary>Deletes all rows from <paramref name="tableName"/> — call in SetUp to start clean.</summary>
    protected async Task ClearAsync(string tableName)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = $@"DELETE FROM ""{tableName}""";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Counts rows in <paramref name="tableName"/>.</summary>
    protected async Task<long> CountAsync(string tableName)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = $@"SELECT COUNT(*) FROM ""{tableName}""";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Runs a raw scalar query and returns the result.</summary>
    protected async Task<object?> ScalarAsync(string sql)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }
}
