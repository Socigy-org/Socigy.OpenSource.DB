# Querying

The source generator produces a strongly-typed `Query()` builder on every `[Table]` class. Queries are translated from C# lambda expressions to parameterised SQL at runtime.

## Basic query (all rows)

```csharp
var users = await User.Query()
    .WithConnection(conn)
    .ExecuteAsync()
    .ToListAsync();
```

`ExecuteAsync()` returns an `IAsyncEnumerable<T>`. Use `.ToListAsync()` (from `System.Linq.Async`) or iterate with `await foreach`.

## WHERE expressions

Pass a predicate lambda to `Query()` or call `.Where()` on the builder:

```csharp
// Inline predicate
var active = await User.Query(x => x.IsActive == true)
    .WithConnection(conn)
    .ExecuteAsync()
    .ToListAsync();

// Chained
var results = await User.Query()
    .Where(x => x.Priority >= 10 && x.Priority <= 50)
    .WithConnection(conn)
    .ExecuteAsync()
    .ToListAsync();
```

### Supported operators

| C# expression | SQL |
|---------------|-----|
| `x.Col == value` | `"col" = $n` |
| `x.Col != value` | `"col" <> $n` |
| `x.Col > value` | `"col" > $n` |
| `x.Col >= value` | `"col" >= $n` |
| `x.Col < value` | `"col" < $n` |
| `x.Col <= value` | `"col" <= $n` |
| `x.Col == null` | `"col" IS NULL` |
| `x.Col != null` | `"col" IS NOT NULL` |
| `a && b` | `a AND b` |
| `a \|\| b` | `a OR b` |
| `!a` | `NOT (a)` |

### Flagged enum — HasFlag

When a property carries `[FlaggedEnum]`, you can filter by flag membership:

```csharp
// Users who have the Admin role
var admins = await User.Query(x => x.Role.HasFlag(TestRole.Admin))
    .WithConnection(conn)
    .ExecuteAsync()
    .ToListAsync();
```

This translates to an `EXISTS` sub-query against the junction table.

## ORDER BY

```csharp
// Ascending (default)
var asc = await User.Query()
    .WithConnection(conn)
    .OrderBy(x => new object[] { x.CreatedAt })
    .ExecuteAsync()
    .ToListAsync();

// Descending
var desc = await User.Query()
    .WithConnection(conn)
    .OrderByDesc(x => new object[] { x.Priority })
    .ExecuteAsync()
    .ToListAsync();
```

Multiple columns:

```csharp
.OrderBy(x => new object[] { x.LastName, x.FirstName })
```

## LIMIT and OFFSET

```csharp
// First 10 rows
var page1 = await User.Query()
    .WithConnection(conn)
    .Limit(10)
    .ExecuteAsync()
    .ToListAsync();

// Second page of 10
var page2 = await User.Query()
    .WithConnection(conn)
    .OrderBy(x => new object[] { x.CreatedAt })
    .Limit(10)
    .Offset(10)
    .ExecuteAsync()
    .ToListAsync();
```

Passing a negative value to `Limit` or `Offset` throws `ArgumentOutOfRangeException`.

## Combining everything

```csharp
var results = await User.Query(x => x.IsActive == true && x.CreatedAt > cutoff)
    .WithConnection(conn)
    .OrderByDesc(x => new object[] { x.CreatedAt })
    .Limit(25)
    .Offset(0)
    .ExecuteAsync()
    .ToListAsync();
```
