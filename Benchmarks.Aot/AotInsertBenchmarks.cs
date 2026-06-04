using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Benchmarks; // BenchWrite + BenchSupport
using Npgsql;

namespace Benchmarks.Aot;

/// <summary>
/// NativeAOT single-row INSERT benchmark: Socigy insert builder vs hand-written ADO.NET. Proves the
/// generated write path runs under NativeAOT. The table is truncated between iterations.
/// </summary>
[MemoryDiagnoser]
public class AotInsertBenchmarks
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

    [Benchmark(Description = "Raw ADO.NET (hand-written)")]
    public async Task<int> RawAdoNet()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO bench_writes (id, name, age) VALUES (@id, @name, @age)";
        cmd.Parameters.Add(new NpgsqlParameter("id", Guid.NewGuid()));
        cmd.Parameters.Add(new NpgsqlParameter("name", "x"));
        cmd.Parameters.Add(new NpgsqlParameter("age", 1));
        return await cmd.ExecuteNonQueryAsync();
    }
}
