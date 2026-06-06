using System;
using System.Threading.Tasks;
using Npgsql;

namespace Benchmarks;

/// <summary>
/// Connection-string resolution and one-time schema/seed for the benchmark database.
/// Set the connection string via the <c>BENCH_DB</c> environment variable, otherwise a local default
/// (matching the test/CI Postgres) is used.
/// </summary>
public static class BenchSupport
{
    public const int SeedRows = 1000;

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("BENCH_DB")
        ?? "Host=localhost;Port=5432;Username=postgres;Password=1234;Database=postgres;Pooling=true;Maximum Pool Size=50";

    /// <summary>Creates the write-benchmark table <c>bench_writes</c> (idempotent).</summary>
    public static async Task EnsureWriteTableAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS ""bench_writes"" (
                ""id""   UUID    NOT NULL PRIMARY KEY,
                ""name"" TEXT    NOT NULL,
                ""age""  INTEGER NOT NULL
            );";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Empties <c>bench_writes</c> (used between insert-benchmark iterations).</summary>
    public static void TruncateWrites(string connectionString)
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"TRUNCATE ""bench_writes""";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Ensures a single known row exists in <c>bench_writes</c> for the update benchmark.</summary>
    public static async Task EnsureUpdateRowAsync(string connectionString, Guid id)
    {
        await EnsureWriteTableAsync(connectionString);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO ""bench_writes"" (""id"", ""name"", ""age"") VALUES (@id, 'seed', 0)
                            ON CONFLICT (""id"") DO NOTHING";
        cmd.Parameters.Add(new NpgsqlParameter("id", id));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Ensures <c>bench_users</c> is seeded, then creates <c>bench_logins</c> with one login per user (idempotent). For the JOIN benchmark.</summary>
    public static async Task EnsureJoinSeedAsync(string connectionString)
    {
        await EnsureSeedAsync(connectionString);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        await using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = @"
                CREATE TABLE IF NOT EXISTS ""bench_logins"" (
                    ""id""      UUID      NOT NULL PRIMARY KEY,
                    ""user_id"" UUID      NOT NULL,
                    ""seen_at"" TIMESTAMP NOT NULL DEFAULT NOW()
                );";
            await ddl.ExecuteNonQueryAsync();
        }

        await using (var count = conn.CreateCommand())
        {
            count.CommandText = @"SELECT COUNT(*) FROM ""bench_logins""";
            if (Convert.ToInt64(await count.ExecuteScalarAsync()) >= SeedRows) return;
        }

        await using (var truncate = conn.CreateCommand())
        {
            truncate.CommandText = @"TRUNCATE ""bench_logins""";
            await truncate.ExecuteNonQueryAsync();
        }

        // One login per user, keyed by the existing user ids so the join is 1:1.
        await using var insert = conn.CreateCommand();
        insert.CommandText = @"INSERT INTO ""bench_logins"" (""id"", ""user_id"", ""seen_at"")
                               SELECT gen_random_uuid(), ""id"", NOW() FROM ""bench_users""";
        await insert.ExecuteNonQueryAsync();
    }

    /// <summary>Creates the <c>bench_users</c> table and seeds it with <see cref="SeedRows"/> rows (idempotent).</summary>
    public static async Task EnsureSeedAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        await using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = @"
                CREATE TABLE IF NOT EXISTS ""bench_users"" (
                    ""id""         UUID      NOT NULL PRIMARY KEY,
                    ""name""       TEXT      NOT NULL,
                    ""age""        INTEGER   NOT NULL,
                    ""created_at"" TIMESTAMP NOT NULL DEFAULT NOW()
                );";
            await ddl.ExecuteNonQueryAsync();
        }

        await using (var count = conn.CreateCommand())
        {
            count.CommandText = @"SELECT COUNT(*) FROM ""bench_users""";
            var existing = Convert.ToInt64(await count.ExecuteScalarAsync());
            if (existing >= SeedRows) return;
        }

        await using (var truncate = conn.CreateCommand())
        {
            truncate.CommandText = @"TRUNCATE ""bench_users""";
            await truncate.ExecuteNonQueryAsync();
        }

        // age = row index (0..SeedRows-1) so "WHERE age < N" returns exactly N rows.
        await using var tx = await conn.BeginTransactionAsync();
        await using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            // created_at is populated by the column DEFAULT NOW() — avoids a DateTime.Kind/timestamp mismatch
            // (Npgsql rejects a UTC DateTime written to 'timestamp without time zone').
            insert.CommandText = @"INSERT INTO ""bench_users"" (""id"", ""name"", ""age"") VALUES (@id, @name, @age)";
            var pId = insert.Parameters.Add(new NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid));
            var pName = insert.Parameters.Add(new NpgsqlParameter("name", NpgsqlTypes.NpgsqlDbType.Text));
            var pAge = insert.Parameters.Add(new NpgsqlParameter("age", NpgsqlTypes.NpgsqlDbType.Integer));
            await insert.PrepareAsync();

            for (int i = 0; i < SeedRows; i++)
            {
                pId.Value = Guid.NewGuid();
                pName.Value = "user_" + i;
                pAge.Value = i;
                await insert.ExecuteNonQueryAsync();
            }
        }
        await tx.CommitAsync();
    }
}
