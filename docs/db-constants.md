# DbDefaults & DbValues

Both classes live in `Socigy.OpenSource.DB.Attributes` and provide cross-platform sentinel constants. Each constant is translated to the correct SQL expression or keyword for the target database engine by the migration tool — so your C# code stays portable even if you switch engines.

---

## `DbDefaults`

Use with `[Default(…)]` to declare a DB-side default value.

```csharp
[Default(DbDefaults.Guid.Random)]
public Guid Id { get; set; }
```

### `DbDefaults.Guid`

| Constant | PostgreSQL SQL |
|----------|----------------|
| `DbDefaults.Guid.Random` | `gen_random_uuid()` |
| `DbDefaults.Guid.Sequential` | `uuid_generate_v1mc()` |

### `DbDefaults.Time`

| Constant | PostgreSQL SQL |
|----------|----------------|
| `DbDefaults.Time.Now` | `timezone('utc', now())` |
| `DbDefaults.Time.NowLocal` | `now()` |
| `DbDefaults.Time.Date` | `current_date` |

### `DbDefaults.Bool`

| Constant | PostgreSQL SQL |
|----------|----------------|
| `DbDefaults.Bool.True` | `TRUE` |
| `DbDefaults.Bool.False` | `FALSE` |

### `DbDefaults.Number`

| Constant | PostgreSQL SQL |
|----------|----------------|
| `DbDefaults.Number.Zero` | `0` |
| `DbDefaults.Number.One` | `1` |

### `DbDefaults.Text`

| Constant | PostgreSQL SQL |
|----------|----------------|
| `DbDefaults.Text.Empty` | `''` |

---

## `DbValues`

Use with `[ForeignKey]` referential action properties.

### `DbValues.ForeignKey`

Use with `OnDelete` and `OnUpdate` on `[ForeignKey]`:

```csharp
[ForeignKey(typeof(User), OnDelete = DbValues.ForeignKey.Cascade)]
public Guid UserId { get; set; }

[ForeignKey(typeof(Order),
    OnDelete = DbValues.ForeignKey.SetNull,
    OnUpdate = DbValues.ForeignKey.NoAction)]
public Guid? OrderId { get; set; }
```

| Constant | PostgreSQL SQL | Meaning |
|----------|----------------|---------|
| `DbValues.ForeignKey.Cascade` | `CASCADE` | Delete/update child rows when parent is deleted/updated |
| `DbValues.ForeignKey.SetNull` | `SET NULL` | Set FK column(s) to NULL |
| `DbValues.ForeignKey.SetDefault` | `SET DEFAULT` | Set FK column(s) to their default value |
| `DbValues.ForeignKey.Restrict` | `RESTRICT` | Prevent deletion if child rows exist |
| `DbValues.ForeignKey.NoAction` | `NO ACTION` | Like Restrict but deferred until end of transaction |

> Raw strings (`OnDelete = "CASCADE"`) still work but are not translated and will be passed through to SQL as-is, breaking portability.
