using System;
using Socigy.OpenSource.DB.Attributes;

namespace Benchmarks;

/// <summary>
/// Same column shape as <see cref="BenchUser"/>, but declared as a <c>[TableType]</c> so its table name is
/// bound at runtime (<c>DynamicTable&lt;T&gt;</c>). Lets the benchmark compare the typed/defined path against
/// the dynamic path on the very same <c>bench_users</c> table.
/// </summary>
[TableType]
public partial class BenchUserType
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public int Age { get; set; }

    public DateTime CreatedAt { get; set; }
}
