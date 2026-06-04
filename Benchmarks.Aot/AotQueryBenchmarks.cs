using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Benchmarks; // BenchUser + BenchSupport (linked from the JIT benchmark project)
using Npgsql;

namespace Benchmarks.Aot;

/// <summary>
/// NativeAOT-viable query benchmark: a filtered SELECT materialized into objects, comparing
/// <b>Socigy (typed Query)</b> against a <b>hand-written ADO.NET</b> baseline.
///
/// Dapper and EF Core are intentionally absent: Dapper materializes via <c>System.Reflection.Emit</c>
/// (runtime IL generation) and EF Core's query pipeline relies on runtime codegen — neither works under
/// NativeAOT. Raw ADO.NET is the true AOT-safe floor; Socigy's generated <c>ConvertFrom</c> needs no
/// reflection/codegen, which is what makes it AOT-friendly.
/// </summary>
[MemoryDiagnoser]
public class AotQueryBenchmarks
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
