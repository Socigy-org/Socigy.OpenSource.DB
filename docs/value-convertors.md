# Value Convertors

Value convertors let you intercept the translation between a C# property value and its database representation. Attach `[ValueConvertor(typeof(T))]` to any column property and the source generator will call your convertor when inserting, updating, and reading back rows.

## Defining a convertor

Implement `IDbValueConvertor<TFrom>` from `Socigy.OpenSource.DB.Core.Convertors`:

```csharp
using Socigy.OpenSource.DB.Core.Convertors;

public class UpperCaseStringConvertor : IDbValueConvertor<string>
{
    // Called before INSERT / UPDATE — transforms the C# value for the database
    public object? ConvertToDbValue(string? value) => value?.ToUpperInvariant();

    // Called after SELECT — transforms the raw database value back to C#
    public string? ConvertFromDbValue(object? dbValue) => dbValue?.ToString();
}
```

`ConvertToDbValue` returns `object?` so you can change the stored type entirely (e.g. encrypt a string to a `byte[]`). `ConvertFromDbValue` receives the raw value that came back from `DbDataReader.GetValue()`.

## Attaching a convertor

### `[ValueConvertor]` attribute (recommended)

Apply it directly to the property:

```csharp
[Table("users")]
public partial class User
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    /// <summary>Stored as upper-case in the database.</summary>
    [ValueConvertor(typeof(UpperCaseStringConvertor))]
    public string Username { get; set; } = "";
}
```

### `[Column(ValueConvertor = typeof(T))]` named argument

Alternatively, combine the column name override and convertor in a single attribute:

```csharp
[Column("user_name", ValueConvertor = typeof(UpperCaseStringConvertor))]
public string Username { get; set; } = "";
```

`[ValueConvertor]` takes precedence if both forms are present on the same property.

## What the generator produces

For a convertor-annotated column, `GetColumns()` returns a `ColumnInfo` whose `Value` and `SetValue` go through the convertor:

```csharp
// Generated (simplified) inside GetColumns():
{
    UsernameColumnName,
    new ColumnInfo
    {
        Value    = new UpperCaseStringConvertor().ConvertToDbValue(Username),
        SetValue = v => Username = (string)new UpperCaseStringConvertor().ConvertFromDbValue(v)
    }
}
```

Because every write path (insert, `WithAllFields`, `ExceptFields`, `WithFields`) reads column values through `ColumnInfo.Value`, the convertor is applied consistently without any extra code on your part.

The constructor `(DbDataReader reader, ...)` also calls the convertor:

```csharp
// Generated inside the reader constructor:
Username = ReadValueConvertor<string, UpperCaseStringConvertor>(
    reader, UsernameColumnName, columnOverrides);
```

## Full insert / query / update example

```csharp
// Insert — "alice" is stored as "ALICE"
var user = new User { Username = "alice" };
await user.Insert()
    .WithConnection(conn)
    .ExcludeAutoFields()
    .ExecuteAsync();

// Query — value comes back as "ALICE" (whatever PostgreSQL stored)
await foreach (var u in User.Query(x => x.Id == user.Id).WithConnection(conn).ExecuteAsync())
{
    Console.WriteLine(u.Username); // "ALICE"
}

// Update via WithAllFields — "bob" is stored as "BOB"
user.Username = "bob";
await user.Update().WithConnection(conn).WithAllFields().ExecuteAsync();

// Update via WithFields — convertor still applies
user.Username = "charlie";
await user.Update()
    .WithConnection(conn)
    .WithFields(x => new object[] { x.Username })
    .ExecuteAsync();
```

## Changing the stored type

A convertor can map a C# type to a completely different database type. For example, storing a `string` as `byte[]` for encryption:

```csharp
public class AesEncryptedStringConvertor : IDbValueConvertor<string>
{
    public object? ConvertToDbValue(string? value)
        => value == null ? null : EncryptionHelper.Encrypt(value); // returns byte[]

    public string? ConvertFromDbValue(object? dbValue)
        => dbValue == null ? null : EncryptionHelper.Decrypt((byte[])dbValue);
}
```

> **Migration note:** The migration tool uses the C# property type to infer the column's SQL type. When your convertor changes the stored type (e.g. `string` → `byte[]`), annotate the property with `[Column(Type = "BYTEA")]` (or the appropriate SQL type) to guide the migration tool.

## Inspecting convertor columns at runtime

`GetColumns()` still returns `typeof(string)` as the column's `Type` because that is the database representation type (the value returned by `ConvertToDbValue`). Use the generated constant `UsernameColumnName` to look up the column:

```csharp
var cols = new User().GetColumns();
var colInfo = cols[User.UsernameColumnName];

// colInfo.Value    → result of ConvertToDbValue(currentUsername)
// colInfo.SetValue → lambda that calls ConvertFromDbValue and assigns the property
```

## Compatibility

- Convertor columns work with all write paths: `Insert()`, `Update().WithAllFields()`, `Update().WithFields(...)`, `Update().ExceptFields(...)`.
- Convertor columns work with all read paths: `Query()`, `ConvertFrom(reader)`, and any JOIN or set-operation query that returns this table type.
- `[ValueConvertor]` and `[JsonColumn]` / `[RawJsonColumn]` are mutually exclusive on the same property — JSON columns have their own serialization logic.

## See also

- [Defining Tables](defining-tables.md) — full attribute reference
- [CRUD Operations](crud-operations.md) — insert / update / delete builders
- [JSON Columns](json-columns.md) — typed and raw JSON storage
