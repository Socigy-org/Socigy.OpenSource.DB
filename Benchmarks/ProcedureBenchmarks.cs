using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Benchmarks;

/// <summary>
/// Stored-procedure / mapped-SQL benchmark: the same filtered read expressed as a Socigy <c>.sql</c>
/// procedure (generated <c>Procedures.GetUsersUnderAge</c>), Dapper raw SQL, and EF Core
/// <c>FromSqlRaw</c>. Because the procedure SQL is fixed at build time, Socigy does no runtime
/// expression translation here — this isolates pure execution + materialization.
/// </summary>
[MemoryDiagnoser]
public class ProcedureBenchmarks
{
    [Params(1, 100, 1000)]
    public int Rows;

    private string _cs = "";

    [GlobalSetup]
    public async Task Setup()
    {
        _cs = BenchSupport.ConnectionString;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await BenchSupport.EnsureSeedAsync(_cs);
    }

    [Benchmark(Baseline = true, Description = "Socigy (.sql procedure)")]
    public async Task<int> Socigy()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();

        var list = new List<BenchUser>();
        await foreach (var u in global::Benchmarks.Socigy.Generated.Procedures.GetUsersUnderAge(conn, Rows))
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
        return rows.Count();
    }

    [Benchmark(Description = "EF Core (FromSqlRaw, no tracking)")]
    public async Task<int> EfCoreFromSql()
    {
        await using var ctx = new BenchDbContext(_cs);
        var list = await ctx.Users
            .FromSqlRaw("SELECT id, name, age, created_at FROM bench_users WHERE age < {0}", Rows)
            .AsNoTracking()
            .ToListAsync();
        return list.Count;
    }
}
