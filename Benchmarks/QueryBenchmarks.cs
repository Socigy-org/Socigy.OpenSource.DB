using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Benchmarks;

/// <summary>
/// End-to-end query benchmark: execute a filtered SELECT and materialize the rows into objects.
/// Measures execution + conversion combined for Socigy, Dapper and EF Core (tracking and no-tracking).
/// Requires a live PostgreSQL (set <c>BENCH_DB</c>). The predicate <c>age &lt; Rows</c> returns exactly
/// <see cref="Rows"/> rows from a fixed seed.
/// </summary>
[MemoryDiagnoser]
public class QueryBenchmarks
{
    [Params(1, 100, 1000)]
    public int Rows;

    private string _cs = "";

    [GlobalSetup]
    public async Task Setup()
    {
        _cs = BenchSupport.ConnectionString;
        DefaultTypeMap.MatchNamesWithUnderscores = true; // Dapper: created_at -> CreatedAt
        await BenchSupport.EnsureSeedAsync(_cs);
    }

    [Benchmark(Baseline = true, Description = "Socigy (typed Query)")]
    public async Task<int> Socigy()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();

        var list = new List<BenchUser>();
        await foreach (var u in BenchUser.Query(x => x.Age < Rows).WithConnection(conn).ExecuteAsync())
            list.Add(u);
        return list.Count;
    }

    [Benchmark(Description = "Dapper (raw SQL)")]
    public async Task<int> Dapper_()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();

        var rows = await conn.QueryAsync<BenchUser>(
            @"SELECT id, name, age, created_at FROM bench_users WHERE age < @t", new { t = Rows });
        return System.Linq.Enumerable.Count(rows);
    }

    [Benchmark(Description = "EF Core (no tracking)")]
    public async Task<int> EfCoreNoTracking()
    {
        await using var ctx = new BenchDbContext(_cs);
        var list = await ctx.Users.AsNoTracking().Where(x => x.Age < Rows).ToListAsync();
        return list.Count;
    }

    [Benchmark(Description = "EF Core (tracking)")]
    public async Task<int> EfCoreTracking()
    {
        await using var ctx = new BenchDbContext(_cs);
        var list = await ctx.Users.Where(x => x.Age < Rows).ToListAsync();
        return list.Count;
    }
}
