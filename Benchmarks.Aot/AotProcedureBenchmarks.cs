using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Benchmarks; // BenchUser + BenchSupport
using Npgsql;

namespace Benchmarks.Aot;

/// <summary>
/// NativeAOT procedure benchmark: a Socigy generated <c>.sql</c> procedure vs hand-written ADO.NET.
/// Procedures are fully source-generated (no runtime translation or reflection for materialization),
/// so this path is the most AOT-friendly part of the library.
/// </summary>
[MemoryDiagnoser]
public class AotProcedureBenchmarks
{
    [Params(1, 100, 1000)]
    public int Rows;

    private string _cs = "";

    [GlobalSetup]
    public async Task Setup()
    {
        _cs = BenchSupport.ConnectionString;
        await BenchSupport.EnsureSeedAsync(_cs);
    }

    [Benchmark(Baseline = true, Description = "Socigy (.sql procedure)")]
    public async Task<int> Socigy()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();

        var list = new List<BenchUser>();
        await foreach (var u in global::Benchmarks.Aot.Socigy.Generated.Procedures.GetUsersUnderAge(conn, Rows))
            list.Add(u);
        return list.Count;
    }

    [Benchmark(Description = "Raw ADO.NET (hand-written)")]
    public async Task<int> RawAdoNet()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, age, created_at FROM bench_users WHERE age < @t";
        cmd.Parameters.Add(new NpgsqlParameter("t", Rows));

        var list = new List<BenchUser>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new BenchUser
            {
                Id = reader.GetGuid(0),
                Name = reader.GetString(1),
                Age = reader.GetInt32(2),
                CreatedAt = reader.GetDateTime(3),
            });
        }
        return list.Count;
    }
}
