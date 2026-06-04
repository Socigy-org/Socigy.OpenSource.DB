using System;
using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using Npgsql;
using Socigy.OpenSource.DB.Core.Delegates;
using Socigy.OpenSource.DB.Core.Parsers.Postgresql;

namespace Benchmarks;

/// <summary>
/// No-database micro-benchmark of Socigy's LINQ-expression → parameterized-SQL translation (the per-query
/// "parse" cost). There is no like-for-like competitor: Dapper takes raw SQL (zero translation) and EF
/// Core compiles the LINQ tree once and caches it. This isolates Socigy's translation overhead per call.
/// </summary>
[MemoryDiagnoser]
public class ParseBenchmarks
{
    private static readonly string Name = "alice";

    // Representative predicate: comparison + AND + captured variable + escaped LIKE.
    private static readonly Expression<Func<BenchUser, bool>> Predicate =
        x => x.Age > 18 && x.Name.Contains(Name);

    private static readonly GetColumnName Columns = n => "\"" + n + "\"";

    [Benchmark(Description = "Socigy: expression -> parameterized SQL")]
    public string TranslateWhere()
    {
        using var cmd = new NpgsqlCommand();
        var visitor = new PostgresqlWhereVisitor(Predicate.Parameters[0], Columns, cmd);
        return visitor.Parse(Predicate.Body);
    }
}
