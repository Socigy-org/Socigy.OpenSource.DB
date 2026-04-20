# Sequences & AutoIncrement

## `[AutoIncrement]`

Marks an integer column as auto-incrementing. The generator:

1. Creates a named PostgreSQL sequence (`{table}_{column}_seq`)
2. Sets `DEFAULT nextval('...')` on the column in the migration DDL
3. Skips the column in `INSERT` statements by default (acts like `[Default]`)
4. Exposes a typed sequence helper class on the parent table

```csharp
[Table("counters")]
public partial class Counter
{
    [PrimaryKey]
    public Guid Id { get; set; }

    [AutoIncrement]
    public int Seq { get; set; }

    public string Label { get; set; } = "";
}
```

## Inserting with AutoIncrement

Because `[AutoIncrement]` implies a DB-side default, the column is excluded from INSERT automatically:

```csharp
var counter = new Counter { Id = Guid.NewGuid(), Label = "my-counter" };

await counter.Insert()
    .WithConnection(conn)
    .ExecuteAsync();
// INSERT INTO "counters" ("id", "label") VALUES ($1, $2)
// "seq" is omitted — the DB assigns it
```

## Sequence helper

The generator creates a static `SeqSequence` class (named `{PropertyName}Sequence`) with two methods:

### `GetNextValueAsync`

Advances the sequence and returns the next value:

```csharp
long next = await Counter.SeqSequence.GetNextValueAsync(conn);
```

### `PeekCurrentValueAsync`

Returns the current value without advancing the sequence:

```csharp
long current = await Counter.SeqSequence.PeekCurrentValueAsync(conn);
```

## Pre-allocating sequence values

You can pre-advance the sequence to reserve a block of IDs before a bulk insert:

```csharp
// Reserve 100 values
for (int i = 0; i < 100; i++)
    await Counter.SeqSequence.GetNextValueAsync(conn);

long nextSlot = await Counter.SeqSequence.PeekCurrentValueAsync(conn);
```

## Reading the auto-assigned value after INSERT

Use `WithValuePropagation()` to have the DB-assigned sequence value written back to the instance:

```csharp
var counter = new Counter { Id = Guid.NewGuid(), Label = "x" };

await counter.Insert()
    .WithConnection(conn)
    .WithValuePropagation()
    .ExecuteAsync();

Console.WriteLine(counter.Seq);   // now holds the DB-assigned value
```
