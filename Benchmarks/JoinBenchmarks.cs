using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Benchmarks;

/// <summary>
/// JOIN benchmark: a 1:1 inner join of <c>bench_users</c> to <c>bench_logins</c> filtered to
/// <c>age &lt; Rows</c>, materializing both sides. Compares Socigy's typed join builder, Dapper's
/// multi-mapping join, and EF Core's no-tracking <c>Join</c>. Requires a live PostgreSQL (set <c>BENCH_DB</c>).
/// </summary>
[MemoryDiagnoser]
public class JoinBenchmarks
{
    [Params(1, 100, 1000)]
    public int Rows;

    private string _cs = "";

    [GlobalSetup]
    public async Task Setup()
    {
        _cs = BenchSupport.ConnectionString;
        DefaultTypeMap.MatchNamesWithUnderscores = true; // Dapper: created_at -> CreatedAt, user_id -> UserId
        await BenchSupport.EnsureJoinSeedAsync(_cs);
    }

    [Benchmark(Baseline = true, Description = "Socigy (typed Join)")]
    public async Task<int> Socigy()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();

        int n = 0;
        await foreach (var (user, login) in BenchUser.Query(u => u.Age < Rows)
            .Join<BenchLogin>((u, l) => u.Id == l.UserId)
            .WithConnection(conn)
            .ExecuteAsync())
        {
            _ = (user, login);
            n++;
        }
        return n;
    }

    [Benchmark(Description = "Dapper (multi-map join)")]
    public async Task<int> Dapper_()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();

        var rows = await conn.QueryAsync<BenchUser, BenchLogin, int>(
            @"SELECT u.id, u.name, u.age, u.created_at, l.id, l.user_id, l.seen_at
              FROM bench_users u INNER JOIN bench_logins l ON u.id = l.user_id
              WHERE u.age < @t",
            (u, l) => 1, new { t = Rows }, splitOn: "id");
        return rows.Count();
    }

    [Benchmark(Description = "EF Core (no tracking join)")]
    public async Task<int> EfCore()
    {
        await using var ctx = new BenchDbContext(_cs);
        var list = await ctx.Users.AsNoTracking().Where(u => u.Age < Rows)
            .Join(ctx.Logins, u => u.Id, l => l.UserId, (u, l) => new { u, l })
            .ToListAsync();
        return list.Count;
    }
}
