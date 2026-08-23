using Npgsql;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Core.Bulk;
using Socigy.OpenSource.DB.Core.CommandBuilders;

namespace AotPublishSmoke;

/// <summary>
/// A row shaped like a realistic one: primary key, plain columns, a server default, and an enum —
/// enough for the generated query/insert/update/delete paths to be worth compiling.
/// </summary>
[Table("smoke_rows")]
public partial class SmokeRow
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public int Age { get; set; }

    [Default(DbDefaults.Time.Now)]
    public DateTime CreatedAt { get; set; }

    public SmokeState State { get; set; }
}

public enum SmokeState
{
    Pending = 0,
    Active = 1,
}

public static class Program
{
    /// <summary>
    /// Every generated code path this library ships, reachable from <c>Main</c> so ILC compiles it.
    /// Nothing is executed: the guard is always false at runtime but opaque to the compiler, which is
    /// all that is needed for the call graph to be rooted.
    /// </summary>
    public static async Task Main(string[] args)
    {
        // args.Length is never > 1000, but the compiler cannot prove it, so the body stays reachable.
        if (args.Length > 1000)
            await ExerciseAsync();

        Console.WriteLine("AOT publish smoke: linked.");
    }

    private static async Task ExerciseAsync()
    {
        await using var conn = new NpgsqlConnection("Host=127.0.0.1");
        var id = Guid.NewGuid();
        var row = new SmokeRow { Id = id, Name = "n", Age = 1, State = SmokeState.Active };

        // SELECT — predicate + the AOT-safe string[] projection/ordering overloads.
        await foreach (var _ in SmokeRow.Query(x => x.Age < 10)
                           .Select(nameof(SmokeRow.Id), nameof(SmokeRow.Name))
                           .OrderBy(nameof(SmokeRow.Age))
                           .WithConnection(conn)
                           .ExecuteAsync())
        {
        }

        await foreach (var _ in SmokeRow.Query(x => x.Name == "n" && x.Id != id)
                           .OrderByDesc(nameof(SmokeRow.CreatedAt))
                           .WithConnection(conn)
                           .ExecuteAsync())
        {
        }

        // INSERT — plain, server-defaults, and the string[] keep overload.
        await row.Insert().WithConnection(conn).ExecuteAsync();
        await row.Insert().ExcludeAutoFields(nameof(SmokeRow.CreatedAt)).WithConnection(conn).ExecuteAsync();
        // new string[] rather than a collection expression: a bare [ ... ] is ambiguous against the
        // Expression<Func<T, object?[]>> overload, and picking that one silently defeats the point here.
        var keep = new string[] { nameof(SmokeRow.CreatedAt) };
        await SmokeRow.InsertMultipleAsync([row], conn, keep);

        // Bulk COPY — the string[] keep overload.
        await BulkCopy.InsertMultipleCopyAsync([row], conn, keep);

        // UPDATE — the path through PostgresqlUpdateVisitor, which no other gate here reaches.
        // Both the WithFields and ExceptFields string[] forms.
        await row.Update()
            .WithFields(nameof(SmokeRow.Name), nameof(SmokeRow.Age))
            .Where(x => x.Id == id)
            .WithConnection(conn)
            .ExecuteAsync();

        await row.Update()
            .ExceptFields(nameof(SmokeRow.CreatedAt))
            .Where(x => x.Id == id)
            .WithConnection(conn)
            .ExecuteAsync();

        // Note there is deliberately NO `WithFields(x => new object?[] { ... })` anywhere in this app.
        // Any expression *selector* compiles to Expression.NewArrayInit, which is [RequiresDynamicCode]
        // and is an application-side IL3050, fixed by moving to the string[] overload above. Including
        // one here would mask whether the library itself is clean. Predicates (Expression<Func<T,bool>>)
        // are fine and are exercised throughout.

        // An = ANY(@p) collection predicate, which routes through ExpressionEvaluator.ToTypedArray —
        // the method carrying the Array.CreateInstance / MakeGenericType sites.
        var ids = new List<Guid> { id };
        var states = new List<SmokeState?> { SmokeState.Active, null };
        await foreach (var _ in SmokeRow.Query(x => ids.Contains(x.Id) && states.Contains(x.State))
                           .WithConnection(conn)
                           .ExecuteAsync())
        {
        }

        // DELETE.
        await row.Delete().Where(x => x.Id == id).WithConnection(conn).ExecuteAsync();
    }
}
