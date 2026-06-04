using System;
using Socigy.OpenSource.DB.Attributes;

namespace Benchmarks;

/// <summary>
/// Entity for the INSERT/UPDATE benchmarks (table <c>bench_writes</c>). Annotated for Socigy,
/// mapped via fluent config for EF Core, and used directly by Dapper.
/// </summary>
[Table("bench_writes")]
public partial class BenchWrite
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public int Age { get; set; }
}
