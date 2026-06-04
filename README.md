# Socigy.OpenSource.DB

A Roslyn incremental source generator that reads your annotated C# classes at build time and emits a fully typed PostgreSQL data layer — INSERT, SELECT, UPDATE, DELETE, JOINs, set operations, and migrations — without a single line of boilerplate.

**[Full documentation → docs.socigy.com/database](https://docs.socigy.com/database/)**

---

## Installation

```bash
dotnet add package Socigy.OpenSource.DB
```

A single package reference installs the Core runtime, the Roslyn source generator, and the CLI migration tool.

---

## Quick start

**1. Annotate a class**

```csharp
using Socigy.OpenSource.DB.Attributes;

[Table("users")]
public partial class User
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    [StringLength(3, 50), Unique]
    public string Username { get; set; }

    [StringLength(5, 254), Unique]
    public string Email { get; set; }

    public string Status { get; set; } = "active";   // → DEFAULT 'active'

    [Default(DbDefaults.Time.Now)]
    public DateTime CreatedAt { get; set; }
}
```

**2. Build — the generator emits all query methods**

```bash
dotnet build
```

**3. Use the generated methods**

```csharp
// INSERT
var user = new User { Username = "alice", Email = "alice@example.com" };
await user.Insert()
    .WithConnection(conn)
    .ExcludeAutoFields()        // let the DB fill Id and CreatedAt
    .WithValuePropagation()     // write DB-generated values back to the object
    .ExecuteAsync();

// SELECT
await foreach (var u in User.Query(x => x.Status == "active")
    .OrderBy(x => new object[] { x.CreatedAt })
    .Limit(20)
    .WithConnection(conn)
    .ExecuteAsync())
{
    Console.WriteLine(u.Username);
}

// UPDATE
user.Email = "newalice@example.com";
await user.Update()
    .WithConnection(conn)
    .WithFields(x => new object[] { x.Email })
    .ExecuteAsync();

// DELETE
await user.Delete().WithConnection(conn).ExecuteAsync();
```

---

## Features

- **Zero boilerplate** — annotate once, every CRUD method is generated at build time
- **Fully typed** — WHERE clauses, ORDER BY, and field selectors use C# expressions; no raw strings
- **Migrations** — CLI tool analyses your compiled assembly and generates PostgreSQL DDL; a tracking table handles incremental applies
- **JOINs** — `Join`, `LeftJoin`, `RightJoin`, `FullOuterJoin`, `NaturalJoin`, `CrossJoin`
- **Set operations** — `Union`, `UnionAll`, `Intersect`, `IntersectAll`, `Except`, `ExceptAll`
- **Flagged enums** — `[FlaggedEnum]` generates a junction table and typed flag helpers
- **JSON columns** — `[JsonColumn]` and `[RawJsonColumn]` for JSONB with optional AOT-safe typed serialisation
- **Procedure mapping** — write SQL in `.sql` files, get strongly-typed async wrappers at compile time
- **Value convertors** — custom per-column read/write transformation via `IDbValueConvertor<T>`
- **AOT compatible** — no runtime reflection; safe to publish with `PublishAot=true`

---

## DI setup

Add `socigy.json` to your DB class library project root:

```json
{
  "database": {
    "platform": "postgresql",
    "databaseName": "MyDb",
    "generateDbConnectionFactory": true,
    "generateWebAppExtensions": true
  }
}
```

The build generates `AddMyDb()` extension methods and registers `IDbConnectionFactory` and `IMigrationManager` in DI:

```csharp
// Program.cs
builder.AddMyDb();

var app = builder.Build();
await app.EnsureLatestMyDbMigration();   // apply pending migrations on startup
```

Connection strings are read from `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "MyDb": {
      "Default": "Host=localhost;Port=5432;Username=postgres;Password=secret"
    }
  }
}
```

---

## Migrations

Run the migration build configuration to generate DDL from your current model:

```bash
dotnet build -c DB_Migration
```

Migration files land in `Socigy/Migrations/`. Apply them at startup with `EnsureLatestMyDbMigration()` or manage them manually via `IMigrationManager.EnsureLatestVersion()`.

---

## Documentation

Full reference covering every attribute, builder method, join variant, migration option, and DI pattern:

**[docs.socigy.com/database](https://docs.socigy.com/database/)**

| Section | Topics |
|---|---|
| [Getting started](https://docs.socigy.com/database/0.1.82/getting-started/quickstart) | Installation, project structure, `socigy.json` |
| [Defining models](https://docs.socigy.com/database/0.1.82/defining-models/tables) | All attributes, column types, defaults, constraints |
| [Querying](https://docs.socigy.com/database/0.1.82/querying/select) | SELECT, INSERT, UPDATE, DELETE, JOINs, set operations |
| [Migrations](https://docs.socigy.com/database/0.1.82/migration/cli-tool) | CLI tool, schema generation, applying, custom migrations |
| [Advanced](https://docs.socigy.com/database/0.1.82/advanced/procedure-mapping) | Procedure mapping, value convertors, Check DSL |

---

## License

Mozilla Public License 2.0 (MPL-2.0) — see [LICENSE](LICENSE).
