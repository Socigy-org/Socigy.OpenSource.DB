# CHECK Constraints

There are three ways to add a `CHECK` constraint to a table or column.

## 1. Raw SQL string

Pass a literal SQL fragment directly:

```csharp
[Table("users")]
[Check("length(email) < 100")]         // table-level
public partial class User
{
    [Check("length(phone_number) > 7")] // column-level
    public string PhoneNumber { get; set; } = "";
}
```

### Optional constraint name

```csharp
[Check("length(email) < 100", Name = "CK_users_email_len")]
```

## 2. Type-safe `DbCheck` DSL

Because C# attribute arguments must be compile-time constants, method calls like `DbCheck.Len(...)` cannot be used directly in `[Check(…)]`. Instead, implement `IDbCheckExpression` in a class and reference it with `typeof`:

```csharp
// Define the expression provider
public class EmailLenCheck : IDbCheckExpression
{
    public DbCheckExpr Build(string? columnName) =>
        DbCheck.Len(DbCheck.Value(nameof(User.Email)), DbCheck.Operators.LessThan, 100);
}

// Reference it on the class
[Table("users")]
[Check(typeof(EmailLenCheck))]
public partial class User { ... }
```

For column-level checks the generator passes the actual column name into `Build`:

```csharp
public class TagFormatCheck : IDbCheckExpression
{
    public DbCheckExpr Build(string? columnName) =>
        DbCheck.Regex(DbCheck.Column(columnName!), "^[a-z0-9_-]{3,16}$");
}

[Check(typeof(TagFormatCheck))]
public string Tag { get; set; } = "";
```

## `DbCheck` builder reference

### Column references

| Method | Output |
|--------|--------|
| `DbCheck.Value("PhoneNumber")` | `"phone_number"` (converts C# property name to snake_case) |
| `DbCheck.Column("phone_number")` | `"phone_number"` (uses the name as-is) |

### String/pattern functions

| Method | Output |
|--------|--------|
| `DbCheck.Len(col, op, n)` | `length("col") op n` |
| `DbCheck.StartsWith(col, "prefix")` | `"col" LIKE 'prefix%'` |
| `DbCheck.EndsWith(col, "suffix")` | `"col" LIKE '%suffix'` |
| `DbCheck.Contains(col, "sub")` | `"col" LIKE '%sub%'` |
| `DbCheck.Regex(col, "pattern")` | `"col" ~ 'pattern'` |

Single quotes in string arguments are escaped automatically (`'` → `''`).

### Logical operators

| Method | Output |
|--------|--------|
| `DbCheck.Not(expr)` | `NOT (expr)` |
| `DbCheck.And(a, b)` | `(a AND b)` |
| `DbCheck.And(a, b, c, ...)` | `(a AND b AND c ...)` |
| `DbCheck.Or(a, b)` | `(a OR b)` |
| `DbCheck.Or(a, b, c, ...)` | `(a OR b OR c ...)` |
| `DbCheck.Eq(left, right)` | `left = right` |

### Comparison operator tokens

Use `DbCheck.Operators.*` as the `op` argument to `Len`, `Eq`, etc.:

```
DbCheck.Operators.LessThan        →  <
DbCheck.Operators.GreaterThan     →  >
DbCheck.Operators.LessOrEqual     →  <=
DbCheck.Operators.GreaterOrEqual  →  >=
DbCheck.Operators.Equal           →  =
DbCheck.Operators.NotEqual        →  <>
```

### Literals

| Method | Output |
|--------|--------|
| `DbCheck.Literal("active")` | `'active'` |
| `DbCheck.Literal(42L)` | `42` |
| `DbCheck.Literal(3.14)` | `3.14` |

## Composed example

```csharp
public class UserConstraint : IDbCheckExpression
{
    public DbCheckExpr Build(string? _) => DbCheck.And(
        DbCheck.Len(DbCheck.Value(nameof(User.Email)), DbCheck.Operators.LessThan, 255),
        DbCheck.Not(DbCheck.StartsWith(DbCheck.Value(nameof(User.PhoneNumber)), "+420"))
    );
}

[Table("users")]
[Check(typeof(UserConstraint))]
public partial class User { ... }
```

Generates:

```sql
CONSTRAINT "CK_users_..."
CHECK (length("email") < 255 AND NOT ("phone_number" LIKE '+420%'))
```

## `CheckAttribute` properties

| Property | Description |
|----------|-------------|
| `Statement` | Raw SQL string (set when using the `string` constructor) |
| `ExpressionType` | The `IDbCheckExpression` type (set when using `typeof(T)`) |
| `Name` | Optional constraint name (settable on both constructors) |
