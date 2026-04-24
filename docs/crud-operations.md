# CRUD Operations

Every `[Table]`-annotated `partial class` gets generated instance and static methods for inserting, updating, and deleting rows.

All builders extend `SqlCommandBuilder<T>` and require a connection (or transaction) before executing.

## Insert

### Instance builder

```csharp
var user = new User { Username = "alice" };

bool ok = await user.Insert()
    .WithConnection(conn)
    .ExecuteAsync();
```

### `ExcludeAutoFields()`

Skips columns marked with `[Default]` or `[AutoIncrement]`, letting the database apply its own defaults:

```csharp
var user = new User { Username = "alice" };   // Id and CreatedAt have [Default]

await user.Insert()
    .WithConnection(conn)
    .ExcludeAutoFields()
    .ExecuteAsync();
// INSERT INTO "users" ("username") VALUES ($1)
```

### `WithValuePropagation()`

Uses `RETURNING *` to read back the full row after insert and write DB-generated values back to the instance:

```csharp
var user = new User { Username = "alice" };

await user.Insert()
    .WithConnection(conn)
    .ExcludeAutoFields()
    .WithValuePropagation()
    .ExecuteAsync();

Console.WriteLine(user.Id);          // populated by DB
Console.WriteLine(user.CreatedAt);   // populated by DB
```

### Static helper

```csharp
bool ok = await User.InsertAsync(user, conn);
```

### Transaction support

```csharp
await using var tx = await conn.BeginTransactionAsync();
await user.Insert().WithTransaction(tx).ExecuteAsync();
await tx.CommitAsync();
```

## Update

### Static helper (all columns, PK-based WHERE)

```csharp
int rowsAffected = await User.UpdateAsync(user, conn);
```

### Instance builder — all fields

```csharp
int rows = await user.Update()
    .WithConnection(conn)
    .WithAllFields()
    .ExecuteAsync();
```

### Instance builder — all fields except some

Use `.WithAllFields().Except()` to update everything except the listed properties:

```csharp
int rows = await user.Update()
    .WithConnection(conn)
    .WithAllFields()
    .Except(x => new object[] { x.CreatedAt })
    .ExecuteAsync();
// Updates all columns except "created_at"
```

### Instance builder — selected fields only

Use `WithFields` with a lambda returning an array of properties to update:

```csharp
user.Username = "alice2";

int rows = await user.Update()
    .WithConnection(conn)
    .WithFields(x => new object[] { x.Username })
    .ExecuteAsync();
// UPDATE "users" SET "username" = $1 WHERE "id" = $2
```

Multiple fields:

```csharp
await user.Update()
    .WithConnection(conn)
    .WithFields(x => new object[] { x.Username, x.Email })
    .ExecuteAsync();
```

## Delete

### Delete by instance (uses PK)

```csharp
int rows = await user.Delete()
    .WithConnection(conn)
    .ExecuteAsync();
// DELETE FROM "users" WHERE "id" = $1
```

### Delete with a WHERE filter

```csharp
int rows = await User.Delete(x => x.CreatedAt < cutoff)
    .WithConnection(conn)
    .ExecuteAsync();
```

The lambda uses the same expression syntax as the query builder — see [Querying](querying.md).

## Transaction pattern

Any builder accepts `.WithTransaction(tx)` in place of `.WithConnection(conn)`. The connection is derived from the transaction automatically.

```csharp
await using var tx = await conn.BeginTransactionAsync();
try
{
    await userA.Insert().WithTransaction(tx).ExcludeAutoFields().ExecuteAsync();
    await userB.Insert().WithTransaction(tx).ExcludeAutoFields().ExecuteAsync();
    await tx.CommitAsync();
}
catch
{
    await tx.RollbackAsync();
    throw;
}
```

## See also

- [JSON Columns](json-columns.md) — inserting and updating `jsonb` columns with `[RawJsonColumn]` / `[JsonColumn]`
- [Querying](querying.md) — WHERE, ORDER BY, LIMIT / OFFSET
- [Sequences](sequences.md) — reading back auto-increment values after insert
- [Flagged Enums](flagged-enums.md) — junction-table insert / delete helpers
