using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Dapper;
using Npgsql;

namespace Benchmarks;

/// <summary>
/// Single-row INSERT benchmark: Socigy insert builder vs Dapper <c>ExecuteAsync</c> vs EF Core
/// <c>Add</c> + <c>SaveChanges</c>. The <c>bench_writes</c> table is truncated between iterations so
/// rows don't accumulate.
/// </summary>
[MemoryDiagnoser]
public class InsertBenchmarks
{
    private string _cs = "";

    [GlobalSetup]
    public async Task Setup()
    {
        _cs = BenchSupport.ConnectionString;
        await BenchSupport.EnsureWriteTableAsync(_cs);
    }

    [IterationSetup]
    public void ClearTable() => BenchSupport.TruncateWrites(_cs);

    [Benchmark(Baseline = true, Description = "Socigy (insert builder)")]
    public async Task<bool> Socigy()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        var row = new BenchWrite { Id = Guid.NewGuid(), Name = "x", Age = 1 };
        return await row.Insert().WithConnection(conn).ExecuteAsync();
    }

    [Benchmark(Description = "Dapper (ExecuteAsync)")]
    public async Task<int> Dapper_()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        return await conn.ExecuteAsync(
            @"INSERT INTO bench_writes (id, name, age) VALUES (@Id, @Name, @Age)",
            new { Id = Guid.NewGuid(), Name = "x", Age = 1 });
    }

    [Benchmark(Description = "EF Core (Add + SaveChanges)")]
    public async Task<int> EfCore()
    {
        await using var ctx = new BenchDbContext(_cs);
        ctx.Writes.Add(new BenchWrite { Id = Guid.NewGuid(), Name = "x", Age = 1 });
        return await ctx.SaveChangesAsync();
    }
}
