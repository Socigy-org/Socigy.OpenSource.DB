using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Benchmarks;

/// <summary>
/// Single-row UPDATE benchmark (by primary key): Socigy update builder vs Dapper <c>ExecuteAsync</c>
/// vs EF Core <c>ExecuteUpdateAsync</c> (the set-based, no-load update — the fair single-statement
/// comparison). All target one pre-seeded row, so the operation is idempotent and nothing accumulates.
/// </summary>
[MemoryDiagnoser]
public class UpdateBenchmarks
{
    private static readonly Guid RowId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private string _cs = "";

    [GlobalSetup]
    public async Task Setup()
    {
        _cs = BenchSupport.ConnectionString;
        await BenchSupport.EnsureUpdateRowAsync(_cs, RowId);
    }

    [Benchmark(Baseline = true, Description = "Socigy (update builder)")]
    public async Task<int> Socigy()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        var row = new BenchWrite { Id = RowId, Name = "updated", Age = 42 };
        return await row.Update().WithAllFields().WithConnection(conn).ExecuteAsync();
    }

    [Benchmark(Description = "Dapper (ExecuteAsync)")]
    public async Task<int> Dapper_()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        return await conn.ExecuteAsync(
            @"UPDATE bench_writes SET name = @Name, age = @Age WHERE id = @Id",
            new { Id = RowId, Name = "updated", Age = 42 });
    }

    [Benchmark(Description = "EF Core (ExecuteUpdate)")]
    public async Task<int> EfCore()
    {
        await using var ctx = new BenchDbContext(_cs);
        return await ctx.Writes
            .Where(x => x.Id == RowId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "updated").SetProperty(x => x.Age, 42));
    }
}
