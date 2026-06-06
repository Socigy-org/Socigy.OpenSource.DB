using System;
using Socigy.OpenSource.DB.Attributes;

namespace Benchmarks;

/// <summary>
/// Child of <see cref="BenchUser"/> (one login per user) for the JOIN benchmark. Annotated for Socigy,
/// used by Dapper multi-mapping, and mapped via fluent config for EF Core. Columns: id, user_id, seen_at.
/// </summary>
[Table("bench_logins")]
public partial class BenchLogin
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public DateTime SeenAt { get; set; }
}
