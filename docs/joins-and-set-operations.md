# Joins and Set Operations

The query builder exposes fluent factory methods for SQL JOINs and set operations (UNION, INTERSECT, EXCEPT). Both are built on top of the same expression-visitor infrastructure as the regular `WHERE` clause, so lambda predicates work naturally across table types.

## JOINs

JOIN methods are available on every generated `TableQueryBuilder`. They return a `PostgresqlJoinQueryCommandBuilder<T, TJoin>` that yields `(T Left, TJoin Right)` tuples.

### Factory methods

| Method | SQL keyword | ON required |
|--------|-------------|-------------|
| `Join<TJoin>(on)` | `INNER JOIN` | yes |
| `LeftJoin<TJoin>(on)` | `LEFT JOIN` | yes |
| `RightJoin<TJoin>(on)` | `RIGHT JOIN` | yes |
| `FullOuterJoin<TJoin>(on)` | `FULL OUTER JOIN` | yes |
| `NaturalJoin<TJoin>()` | `NATURAL JOIN` | no |
| `CrossJoin<TJoin>()` | `CROSS JOIN` | no |

`TJoin` must be a `[Table]` class (i.e. it must implement `IDbTable` and have a public parameterless constructor — all generated table classes satisfy this automatically).

### Basic INNER JOIN

```csharp
// Join Order → OrderLine on order.Id == orderLine.OrderId
var results = new List<(Order, OrderLine)>();

await foreach (var (order, line) in Order.Query()
    .Join<OrderLine>((order, line) => order.Id == line.OrderId)
    .WithConnection(conn)
    .ExecuteAsync())
{
    results.Add((order, line));
}
```

The generated SQL selects every column from both tables with non-colliding aliases (`t_col`, `j_col`) so columns with the same name (like `id`) do not interfere:

```sql
SELECT t.id AS t_id, t.created_at AS t_created_at, ...,
       j.id AS j_id, j.order_id AS j_order_id, ...
FROM "orders" AS t
INNER JOIN "order_lines" AS j ON (t.id = j.order_id)
```

### LEFT JOIN

```csharp
// All users, with their latest post (or null if they have none)
await foreach (var (user, post) in User.Query()
    .LeftJoin<Post>((user, post) => user.Id == post.AuthorId)
    .WithConnection(conn)
    .ExecuteAsync())
{
    Console.WriteLine($"{user.Username}: {post?.Title ?? "(no posts)"}");
}
```

When the right side has no matching row, the `TJoin` instance in the tuple has default property values (zero / `Guid.Empty` / `null` for reference types).

### FULL OUTER JOIN

```csharp
await foreach (var (a, b) in TableA.Query()
    .FullOuterJoin<TableB>((a, b) => a.Key == b.Key)
    .WithConnection(conn)
    .ExecuteAsync())
{
    // Either side may be the default instance if unmatched
}
```

### NATURAL JOIN

```csharp
// Joins on all columns with the same name in both tables
await foreach (var (a, b) in TableA.Query()
    .NaturalJoin<TableB>()
    .WithConnection(conn)
    .ExecuteAsync())
{ }
```

`NaturalJoin` takes no ON expression — the database determines the join columns.

### CROSS JOIN

```csharp
// Cartesian product — every row in A paired with every row in B
await foreach (var (item, tag) in Item.Query()
    .CrossJoin<Tag>()
    .WithConnection(conn)
    .ExecuteAsync())
{ }
```

### WHERE, LIMIT, and OFFSET on joins

Chain `.Where()` after any join method. The predicate receives both row types:

```csharp
await foreach (var (order, line) in Order.Query()
    .Join<OrderLine>((o, l) => o.Id == l.OrderId)
    .Where((o, l) => o.Status == "shipped" && l.Quantity > 1)
    .Limit(50)
    .Offset(0)
    .WithConnection(conn)
    .ExecuteAsync())
{ }
```

### Supported WHERE operators for joins

The same operators supported by the single-table `WHERE` builder are supported here:

| C# expression | SQL |
|---------------|-----|
| `t.Col == j.Col` | `t.col = j.col` |
| `t.Col == value` | `t.col = $n` |
| `t.Col != null` | `t.col IS NOT NULL` |
| `a && b` | `a AND b` |
| `a \|\| b` | `a OR b` |
| `!a` | `NOT (a)` |
| `t.Name.Contains("x")` | `t.name LIKE '%x%'` |
| `t.Name.StartsWith("x")` | `t.name LIKE 'x%'` |

Closed-over local variables and constant expressions on either side of a comparison are captured as parameters automatically:

```csharp
var minQty = 5;
.Where((order, line) => line.Quantity >= minQty && order.CustomerId == targetId)
```

### Transactions

Pass a transaction instead of a connection:

```csharp
await foreach (var pair in Order.Query()
    .Join<OrderLine>((o, l) => o.Id == l.OrderId)
    .WithTransaction(tx)
    .ExecuteAsync())
{ }
```

---

## Set Operations

Set operations combine two queries of the **same table type** and yield `T` rows. They are built on `PostgresqlSetQueryCommandBuilder<T>` which compiles both sub-queries into a single `DbCommand`, ensuring parameter names never collide.

### Factory methods

| Method | SQL keyword |
|--------|-------------|
| `Union(rhs)` | `UNION` |
| `UnionAll(rhs)` | `UNION ALL` |
| `Intersect(rhs)` | `INTERSECT` |
| `IntersectAll(rhs)` | `INTERSECT ALL` |
| `Except(rhs)` | `EXCEPT` |
| `ExceptAll(rhs)` | `EXCEPT ALL` |

All methods accept another `TableQueryBuilder` as the right-hand side.

### UNION — deduplicated rows from both queries

```csharp
var highPriority = Item.Query().Where(x => x.Priority > 10);
var featured     = Item.Query().Where(x => x.IsFeatured == true);

await foreach (var item in highPriority
    .Union(featured)
    .WithConnection(conn)
    .ExecuteAsync())
{
    Console.WriteLine(item.Name);
}
```

Generated SQL:

```sql
(SELECT * FROM "items" WHERE (priority > $1))
UNION
(SELECT * FROM "items" WHERE (is_featured = $2))
```

### UNION ALL — all rows including duplicates

```csharp
var combined = await Item.Query().Where(x => x.Priority > 5)
    .UnionAll(Item.Query().Where(x => x.Priority < 2))
    .WithConnection(conn)
    .ExecuteAsync()
    .ToListAsync();
```

### INTERSECT — rows common to both queries

```csharp
// Items that are both high priority AND in category X
var highPriority  = Item.Query().Where(x => x.Priority > 10);
var inCategoryX   = Item.Query().Where(x => x.CategoryId == categoryXId);

var results = await highPriority
    .Intersect(inCategoryX)
    .WithConnection(conn)
    .ExecuteAsync()
    .ToListAsync();
```

### EXCEPT — rows in LHS that are not in RHS

```csharp
// All active items except those that have been archived
var active   = Item.Query().Where(x => x.IsActive == true);
var archived = Item.Query().Where(x => x.IsArchived == true);

var results = await active
    .Except(archived)
    .WithConnection(conn)
    .ExecuteAsync()
    .ToListAsync();
```

### LIMIT and OFFSET on set operations

These apply to the **combined** result, not to individual sub-queries:

```csharp
var page = await active
    .Union(scheduled)
    .Limit(20)
    .Offset(40)
    .WithConnection(conn)
    .ExecuteAsync()
    .ToListAsync();
```

Generated SQL:

```sql
(SELECT * FROM "items" WHERE ...) UNION (SELECT * FROM "items" WHERE ...) LIMIT 20 OFFSET 40
```

### How parameter names are kept unique

Both sub-queries are compiled into the **same** `DbCommand` before execution. The first sub-query adds `@p0`, `@p1`, … to the command; the second sub-query continues from where the first left off (`@p2`, `@p3`, …). This means even complex `WHERE` clauses with many parameters on both sides never clash.

---

## Collecting results

Both JOINs and set operations return `IAsyncEnumerable`. Use `await foreach` to stream rows, or `.ToListAsync()` (from the `System.Linq.Async` NuGet package) to collect them:

```csharp
// Stream
await foreach (var row in query.ExecuteAsync())
    Process(row);

// Collect
var list = await query.ExecuteAsync().ToListAsync();

// First match
var first = await query.ExecuteAsync().FirstOrDefaultAsync();
```

## Limitations

- **ORDER BY on JOINs** is not yet supported via the fluent API — add an `ORDER BY` clause directly in the SQL body (e.g. via [Procedure Mapping](procedure-mapping.md)) if you need sorted join results.
- **ORDER BY on set operations** applies to the outer query only; per-sub-query ordering is not supported.
- **Aggregates and GROUP BY** are not supported by the query builder — use [Procedure Mapping](procedure-mapping.md) for queries that require these features.
- The join `TJoin` constraint requires `IDbTable` + `new()`, which all generated `[Table]` classes satisfy. Non-generated types cannot be used as `TJoin`.

## See also

- [Querying](querying.md) — single-table `WHERE`, `ORDER BY`, `LIMIT / OFFSET`
- [Procedure Mapping](procedure-mapping.md) — raw SQL for complex queries
- [CRUD Operations](crud-operations.md) — INSERT, UPDATE, DELETE
