using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Benchmarks; // BenchWrite + BenchSupport
using Npgsql;

namespace Benchmarks.Aot;

/// <summary>
/// NativeAOT selective-UPDATE benchmark: the Socigy update builder (both the expression and the
/// AOT-safe <c>string[]</c> forms of <c>WithFields</c>) vs hand-written ADO.NET.
///
/// This class exists as much for AOT coverage as for timing. The update path runs through
/// <c>PostgresqlUpdateVisitor</c>, which no other benchmark reaches — so this project could report
/// "0 IL warnings" while an application publishing an UPDATE still hit IL2075 from the same assembly.
/// Warnings only appear once the path is reachable, so the path has to be here.
/// </summary>
[MemoryDiagnoser]
public class AotUpdateBenchmarks
{
    private string _cs = "";
    private Guid _id;

    [GlobalSetup]
    public async Task Setup()
    {
        _cs = BenchSupport.ConnectionString;
        await BenchSupport.EnsureWriteTableAsync(_cs);

        // One row to update over and over; UPDATE is idempotent here so no per-iteration reset is needed.
        _id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        var seed = new BenchWrite { Id = _id, Name = "seed", Age = 1 };
        await seed.Insert().WithConnection(conn).ExecuteAsync();
    }

    [Benchmark(Baseline = true, Description = "Socigy (WithFields expression)")]
    public async Task<int> SocigyExpression()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        var row = new BenchWrite { Id = _id, Name = "updated", Age = 2 };
        return await row.Update()
            .WithFields(x => new object?[] { x.Name, x.Age })
            .Where(x => x.Id == _id)
            .WithConnection(conn)
            .ExecuteAsync();
    }

    [Benchmark(Description = "Socigy (WithFields string[] — AOT-safe)")]
    public async Task<int> SocigyColumnNames()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        var row = new BenchWrite { Id = _id, Name = "updated", Age = 2 };
        return await row.Update()
            .WithFields(nameof(BenchWrite.Name), nameof(BenchWrite.Age))
            .Where(x => x.Id == _id)
            .WithConnection(conn)
            .ExecuteAsync();
    }

    [Benchmark(Description = "Raw ADO.NET (hand-written)")]
    public async Task<int> RawAdoNet()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE bench_writes SET name = @name, age = @age WHERE id = @id";
        cmd.Parameters.Add(new NpgsqlParameter("name", "updated"));
        cmd.Parameters.Add(new NpgsqlParameter("age", 2));
        cmd.Parameters.Add(new NpgsqlParameter("id", _id));
        return await cmd.ExecuteNonQueryAsync();
    }
}
