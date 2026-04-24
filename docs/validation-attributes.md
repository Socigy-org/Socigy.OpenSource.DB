# Validation Attributes

These attributes emit `CHECK` constraints in the generated DDL. They are shorthand for the most common column-level validations and require no SQL knowledge.

## Numeric range

### `[Min]`

Emits `CHECK ("col" >= value)`:

```csharp
[Min(0)]
public int Priority { get; set; }

[Min(0.0)]
public double Score { get; set; }

[Min("'2020-01-01'")]   // literal SQL value
public DateTime BirthDate { get; set; }
```

### `[Max]`

Emits `CHECK ("col" <= value)`:

```csharp
[Max(100)]
public int Percentage { get; set; }
```

### `[Bigger]`

Emits `CHECK ("col" > value)` (strictly greater than):

```csharp
[Bigger(0)]
public long Quantity { get; set; }
```

### `[Lower]`

Emits `CHECK ("col" < value)` (strictly less than):

```csharp
[Lower(1000)]
public int Code { get; set; }
```

### `[BiggerOrEqual]` / `[LowerOrEqual]`

Same as `[Min]` / `[Max]` but more explicit in intent:

```csharp
[BiggerOrEqual(18)]
public int Age { get; set; }

[LowerOrEqual(120)]
public int Age { get; set; }
```

### `[Equal]`

Emits `CHECK ("col" = value)`:

```csharp
[Equal(1)]
public int Version { get; set; }
```

## String length

### `[StringLength]`

Controls both the column type and optional minimum length:

```csharp
// VARCHAR(50) — no length check beyond the type limit
[StringLength(50)]
public string Username { get; set; } = "";

// VARCHAR(50) + CHECK (length("username") >= 3)
[StringLength(50, MinLength = 3)]
public string Username { get; set; } = "";
```

Without `[StringLength]`, string columns use the `TEXT` type.

## Unique constraint

### `[Unique]` on a property

Adds a `UNIQUE` constraint on the single column:

```csharp
[Unique]
public string Email { get; set; } = "";
```

### `[Unique]` on a class

Adds a multi-column unique constraint:

```csharp
[Unique(Columns = new[] { "first_name", "last_name" })]
public partial class Person { ... }
```

Optionally name the constraint:

```csharp
[Unique(Columns = new[] { "author_id", "slug" }, Name = "uq_post_slug")]
```

## Nullable

### `[Nullable]`

By default every column is `NOT NULL`. Apply `[Nullable]` to allow `NULL`:

```csharp
[Nullable]
public string? Bio { get; set; }
```

Nullable reference types (`T?`) are automatically detected, so `[Nullable]` is only needed for value types:

```csharp
[Nullable]
public int? OptionalScore { get; set; }
```

## Combining attributes

Multiple validation attributes can be stacked on the same property:

```csharp
[StringLength(50, MinLength = 3), Unique]
public string Username { get; set; } = "";

[Min(0), Max(100)]
public int Percentage { get; set; }
```

## See also

- [Defining Tables](defining-tables.md) — full attribute reference
- [Check Constraints](check-constraints.md) — arbitrary SQL CHECK expressions
- [JSON Columns](json-columns.md) — `[RawJsonColumn]` and `[JsonColumn]` for `jsonb` storage
