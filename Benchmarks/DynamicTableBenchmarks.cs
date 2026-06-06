using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Benchmarks;

/// <summary>
/// Defined vs dynamic read benchmark: the same filtered SELECT + materialization on <c>bench_users</c>, via
/// <list type="bullet">
/// <item>Socigy <b>typed Query</b> — table fixed at build time (<see cref="BenchUser"/>), SQL cached per shape;</item>
/// <item>Socigy <b>DynamicTable</b> — table name bound at runtime (<see cref="BenchUserType"/>), SQL rebuilt per call;</item>
/// <item><b>Dapper</b> — runtime table name interpolated into raw SQL;</item>
/// <item><b>EF Core</b> (no tracking) — included for reference only; EF needs a build-time model, so it
/// cannot actually target a runtime table name (it always reads the statically-mapped table).</item>
/// </list>
/// Requires a live PostgreSQL (set <c>BENCH_DB</c>). The predicate <c>age &lt; Rows</c> returns exactly
/// <see cref="Rows"/> rows from the fixed seed.
/// </summary>
[MemoryDiagnoser]
public class DynamicTableBenchmarks
{
    [Params(1, 100, 1000)]
    public int Rows;

    private string _cs = "";

    // A runtime-resolved table name (not a compile-time constant) — what the dynamic paths bind to.
    private string _table = "bench_users";

    [GlobalSetup]
    public async Task Setup()
    {
        _cs = BenchSupport.ConnectionString;
        DefaultTypeMap.MatchNamesWithUnderscores = true; // Dapper: created_at -> CreatedAt
        await BenchSupport.EnsureSeedAsync(_cs);
    }

    [Benchmark(Baseline = true, Description = "Socigy (typed Query, defined)")]
    public async Task<int> SocigyDefined()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();

        var list = new List<BenchUser>();
        await foreach (var u in BenchUser.Query(x => x.Age < Rows).WithConnection(conn).ExecuteAsync())
            list.Add(u);
        return list.Count;
    }

    [Benchmark(Description = "Socigy (DynamicTable, runtime name)")]
    public async Task<int> SocigyDynamic()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();

        var list = await BenchUserType.WithTableName(_table).WithConnection(conn)
            .Query(x => x.Age < Rows).ToListAsync();
        return list.Count;
    }

    [Benchmark(Description = "Dapper (raw SQL, runtime name)")]
    public async Task<int> Dapper_()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();

        var rows = await conn.QueryAsync<BenchUser>(
            $@"SELECT id, name, age, created_at FROM ""{_table}"" WHERE age < @t", new { t = Rows });
        return rows.Count();
    }

    [Benchmark(Description = "EF Core (no tracking, static model)")]
    public async Task<int> EfCore()
    {
        await using var ctx = new BenchDbContext(_cs);
        var list = await ctx.Users.AsNoTracking().Where(x => x.Age < Rows).ToListAsync();
        return list.Count;
    }
}
