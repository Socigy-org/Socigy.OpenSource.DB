# Socigy.OpenSource.DB

A Roslyn source-generator–powered ORM for PostgreSQL. Define your tables with C# attributes, let the generator produce strongly-typed insert/update/delete/query builders, and use the CLI tool to generate and apply migrations — all without writing a line of SQL by hand.

## Features

- **Attribute-driven schema** — map classes to tables with `[Table]`, `[Column]`, `[PrimaryKey]`, `[Default]`, and more
- **Fluent CRUD builders** — insert, update, delete, and query via generated builder methods
- **Lambda WHERE clauses** — `Query(x => x.Priority > 5 && x.Name != null)` translates to SQL
- **Flagged enums** — N:M junction tables auto-generated from `[FlaggedEnum]` / `[FlaggedEnumTable]` + `[FlagTable]`
- **Auto-increment sequences** — `[AutoIncrement]` exposes typed sequence helpers
- **CHECK constraints** — simple `[Check("sql")]` or type-safe `[Check(typeof(T))]` with the `DbCheck` DSL
- **Validation attributes** — `[Min]`, `[Max]`, `[Bigger]`, `[Lower]`, `[StringLength]`, `[Unique]`, and more
- **Cross-platform constants** — `DbDefaults` and `DbValues` sentinel constants translate to correct SQL per engine
- **Migration tool** — CLI that diffs your schema and generates versioned migration files
- **Value Convertors** — transform C# values on the way in and out of the database with `[ValueConvertor(typeof(T))]`
- **Procedure Mapping** — write raw SQL in `.sql` files and get type-safe C# call wrappers generated automatically
- **Joins & Set Operations** — fluent `Join<T>()`, `LeftJoin<T>()`, `Union()`, `Intersect()`, `Except()`, and more

## Installation

Install via NuGet:

```
dotnet add package Socigy.OpenSource.DB
```

Or in your `.csproj`:

```xml
<PackageReference Include="Socigy.OpenSource.DB" Version="*" />
```

The package includes both the runtime Core library and the Roslyn source generator. No manual project-reference wiring is needed.

## Documentation

| Topic | Description |
|-------|-------------|
| [Getting Started](docs/getting-started.md) | Installation, project setup, `socigy.json`, DI wiring |
| [Defining Tables](docs/defining-tables.md) | All table and column attributes with examples |
| [CRUD Operations](docs/crud-operations.md) | Insert, Update, Delete builders |
| [Querying](docs/querying.md) | Query builder, WHERE expressions, ORDER BY, LIMIT/OFFSET |
| [Flagged Enums](docs/flagged-enums.md) | N:M junction tables, static & instance helpers, in-memory cache, custom junction classes |
| [Sequences & AutoIncrement](docs/sequences.md) | `[AutoIncrement]`, sequence accessors |
| [CHECK Constraints](docs/check-constraints.md) | `[Check]`, `DbCheck` DSL, `IDbCheckExpression` |
| [Validation Attributes](docs/validation-attributes.md) | Min/Max/Bigger/Lower/StringLength/Unique/Nullable/Equal |
| [DbDefaults & DbValues](docs/db-constants.md) | Cross-platform sentinel constants for defaults and FK actions |
| [Migrations](docs/migrations.md) | CLI tool, `socigy.json`, migration workflow, `ILocalMigration` |
| [Value Convertors](docs/value-convertors.md) | Custom read/write transforms with `[ValueConvertor]` |
| [Procedure Mapping](docs/procedure-mapping.md) | Type-safe wrappers generated from `.sql` files |
| [Joins and Set Operations](docs/joins-and-set-operations.md) | Multi-table joins and UNION / INTERSECT / EXCEPT |

## Quick Example

```csharp
// 1. Define your model
[Table("users")]
public partial class User
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    [StringLength(50, MinLength = 3)]
    public string Username { get; set; } = "";

    [Default(DbDefaults.Time.Now)]
    public DateTime CreatedAt { get; set; }
}

// 2. Insert
var user = new User { Username = "alice" };
await user.Insert()
    .WithConnection(conn)
    .ExcludeAutoFields()
    .WithValuePropagation()   // writes DB-generated Id/CreatedAt back to instance
    .ExecuteAsync();

// 3. Query
var users = await User.Query(x => x.Username != null)
    .WithConnection(conn)
    .OrderBy(x => new object[] { x.CreatedAt })
    .Limit(10)
    .ExecuteAsync()
    .ToListAsync();

// 4. Update (selected fields only)
user.Username = "alice2";
await user.Update()
    .WithConnection(conn)
    .WithFields(x => new object[] { x.Username })
    .ExecuteAsync();

// 5. Delete
await user.Delete().WithConnection(conn).ExecuteAsync();
```
