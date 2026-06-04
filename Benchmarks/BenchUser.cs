using System;
using Socigy.OpenSource.DB.Attributes;

namespace Benchmarks;

/// <summary>
/// The benchmark entity. Annotated for Socigy.OpenSource.DB (generates the typed Query API),
/// used as-is by Dapper (column matching with underscores), and mapped via fluent config for EF Core.
/// Columns are snake_case: id, name, age, created_at.
/// </summary>
[Table("bench_users")]
public partial class BenchUser
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public int Age { get; set; }

    public DateTime CreatedAt { get; set; }
}
