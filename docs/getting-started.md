# Getting Started

## Requirements

- .NET 8 or later
- PostgreSQL 14+

## Installation

Install the NuGet package:

```
dotnet add package Socigy.OpenSource.DB
```

Or add it directly to your `.csproj`:

```xml
<PackageReference Include="Socigy.OpenSource.DB" Version="*" />
```

The package bundles both the runtime library and the Roslyn source generator. No additional project references or analyzer wiring is needed — the source generator activates automatically on build.

## `socigy.json`

Place a `socigy.json` file in your DB project root to configure the migration tool:

```json
{
  "database": {
    "platform": "postgresql"
  }
}
```

The tool creates this file with defaults on first run if it is missing.

## Wire up migrations at startup

The source generator produces an `EnsureLatest…Migration()` extension method for each DB project. Call it in your host startup to run any pending migrations:

```csharp
// Program.cs
builder.AddAuthDb();                          // registers IDbConnectionFactory
// ...
await app.EnsureLatestAuthDbMigration();      // applies pending migrations
```

## Dependency Injection

The generator produces a keyed `IDbConnectionFactory` registration. Your connection string is read from `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "AuthDb": {
      "Default": "Host=localhost;Database=mydb;Username=postgres;Password=secret"
    }
  }
}
```

Resolve the factory anywhere in your code:

```csharp
var factory = services.GetRequiredKeyedService<IDbConnectionFactory>("AuthDb");
using var conn = factory.Create();
```

## First migration

The initial migration is **not** generated automatically. You must trigger it explicitly by building with the `DB_Migration` configuration:

```
dotnet build -c DB_Migration
```

On Windows this opens a dialog asking for a migration name. On Linux/macOS the name is auto-generated from the diff content.

Each subsequent build with `DB_Migration` detects schema changes and generates a new migration file if anything has changed.

## Further reading

- [Defining Tables](defining-tables.md) — all column and table attributes
- [CRUD Operations](crud-operations.md) — INSERT, UPDATE, DELETE builders
- [Querying](querying.md) — WHERE, ORDER BY, LIMIT / OFFSET
- [Migrations](migrations.md) — migration lifecycle and detected changes
- [JSON Columns](json-columns.md) — storing structured JSON in `jsonb` columns
- [Flagged Enums](flagged-enums.md) — N:M enum flags with junction tables
- [Sequences](sequences.md) — auto-increment columns
- [Validation Attributes](validation-attributes.md) — `[StringLength]`, `[Min]`, `[Max]`, `[Unique]`
- [Check Constraints](check-constraints.md) — custom CHECK expressions
- [DbDefaults & DbValues](db-constants.md) — built-in default and foreign-key constants
