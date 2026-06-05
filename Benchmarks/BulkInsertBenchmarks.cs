using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Benchmarks;

/// <summary>
/// Bulk INSERT benchmark: Socigy's batched <c>InsertMultipleAsync</c> (one multi-row command per chunk)
/// vs a naive per-row Socigy loop in a single transaction, vs Dapper's per-row <c>ExecuteAsync</c> over a
/// list, vs EF Core <c>AddRange</c> + <c>SaveChanges</c>. The <c>bench_writes</c> table is truncated and a
/// fresh batch is built between iterations so rows don't accumulate.
/// </summary>
[MemoryDiagnoser]
public class BulkInsertBenchmarks
{
    private string _cs = "";
    private List<BenchWrite> _rows = new();

    /// <summary>Rows per batch. Both fit in a single command (rows × 3 cols &lt; 65,535 params).</summary>
    [Params(100, 1000)]
    public int Rows;

    [GlobalSetup]
    public async Task Setup()
    {
        _cs = BenchSupport.ConnectionString;
        await BenchSupport.EnsureWriteTableAsync(_cs);
    }

    [IterationSetup]
    public void Prepare()
    {
        BenchSupport.TruncateWrites(_cs);
        _rows = new List<BenchWrite>(Rows);
        for (int i = 0; i < Rows; i++)
            _rows.Add(new BenchWrite { Id = Guid.NewGuid(), Name = "u" + i, Age = i });
    }

    [Benchmark(Baseline = true, Description = "Socigy (InsertMultipleAsync)")]
    public async Task<int> Socigy_Bulk()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        return await BenchWrite.InsertMultipleAsync(_rows, conn);
    }

    [Benchmark(Description = "Socigy (per-row loop, 1 tx)")]
    public async Task<int> Socigy_Loop()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        int n = 0;
        foreach (var r in _rows)
            if (await r.Insert().WithTransaction(tx).ExecuteAsync()) n++;
        await tx.CommitAsync();
        return n;
    }

    [Benchmark(Description = "Dapper (ExecuteAsync over list)")]
    public async Task<int> Dapper_()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        return await conn.ExecuteAsync(
            "INSERT INTO bench_writes (id, name, age) VALUES (@Id, @Name, @Age)",
            _rows);
    }

    [Benchmark(Description = "EF Core (AddRange + SaveChanges)")]
    public async Task<int> EfCore()
    {
        await using var ctx = new BenchDbContext(_cs);
        ctx.Writes.AddRange(_rows);
        return await ctx.SaveChangesAsync();
    }
}
