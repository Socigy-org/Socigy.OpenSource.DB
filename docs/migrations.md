# Migrations

The `Socigy.OpenSource.DB.Tool` CLI analyses your compiled assembly, diffs the current schema against the saved snapshot, and writes versioned C# migration files.

## How it works

1. Build your DB project with the `DB_Migration` configuration to trigger the tool.
2. The tool reads your `[Table]`-annotated types and builds a current schema model.
3. It compares that model against `Socigy/structure.json` (the last committed snapshot).
4. If there are differences it writes a new migration class to `Socigy/Migrations/` and updates `structure.json`.
5. At application startup, the generated `MigrationManager` runs any pending migrations in order.

> **Important:** The initial migration is **not** generated automatically. You must run a `DB_Migration` build to produce the first migration after defining your initial models.

## Triggering a migration build

```
dotnet build -c DB_Migration
```

On **Windows** a dialog appears asking you to name the migration. On **Linux/macOS** the name is auto-generated from the diff.

## File layout

```
YourDb/
├── socigy.json                          # Tool configuration
└── Socigy/
    ├── structure.json                   # Committed schema snapshot
    ├── .gitignore                       # Ignores dirty/diff scratch files
    └── Migrations/
        ├── Initial_Migration.g.cs       # First migration (you trigger this)
        └── V2_Add_Email_Index.g.cs      # Subsequent migrations
```

## `socigy.json`

```json
{
  "database": {
    "platform": "postgresql"
  }
}
```

The tool creates this file with defaults on first run.

## Migration tracking table

The tool tracks applied migrations in a `_scg_migrations` table it creates automatically. The table has these columns:

| Column | Type | Description |
|--------|------|-------------|
| `id` | `BIGINT` (auto-increment) | Internal PK |
| `human_id` | `TEXT` | The migration name you chose |
| `is_rollback` | `BOOLEAN` | `true` if this entry is a rollback |
| `applied_at` | `TIMESTAMP` | When the migration was applied (DB default) |
| `executed_by` | `TEXT` | Who/what executed the migration |

## Running migrations at startup

```csharp
await app.EnsureLatestAuthDbMigration();
```

This runs all unapplied migrations in order.

## `ILocalMigration` — custom migration logic

When generated SQL is not sufficient (e.g. data backfill), implement `ILocalMigration`:

```csharp
public class BackfillDisplayNames : ILocalMigration
{
    public async Task UpAsync(DbConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE ""users""
            SET ""display_name"" = ""first_name"" || ' ' || ""last_name""
            WHERE ""display_name"" IS NULL";
        await cmd.ExecuteNonQueryAsync();
    }
}
```

## What changes are detected

| Change | Migration action |
|--------|-----------------|
| New table | `CREATE TABLE` |
| Dropped table | `DROP TABLE` |
| New column | `ALTER TABLE … ADD COLUMN` |
| Dropped column | `ALTER TABLE … DROP COLUMN` |
| Column type change | `ALTER TABLE … ALTER COLUMN TYPE` |
| Column renamed (`[Renamed]`) | `ALTER TABLE … RENAME COLUMN` |
| Nullable change | `ALTER TABLE … ALTER COLUMN SET/DROP NOT NULL` |
| Default change | `ALTER TABLE … ALTER COLUMN SET/DROP DEFAULT` |
| `[AutoIncrement]` added | `CREATE SEQUENCE` + `SET DEFAULT nextval(…)` |
| `[AutoIncrement]` removed | `DROP DEFAULT` + `DROP SEQUENCE` |
| New CHECK/UNIQUE constraint | `ALTER TABLE … ADD CONSTRAINT` |
| Dropped constraint | `ALTER TABLE … DROP CONSTRAINT` |
| FK `OnDelete`/`OnUpdate` change | Detected as a constraint change, regenerates the FK |
| `[FlaggedEnum]` removed | `DROP TABLE CASCADE` on the junction table |
| Enum value added/removed | `INSERT` / `DELETE` on the reference table |

## Manual invocation

```bash
dotnet run --project Socigy.OpenSource.DB.Tool -- generate \
  --target-assembly ./bin/Release/net8.0/YourDb.dll \
  --project-dir ./YourDb \
  --migrate
```

Omit `--migrate` to regenerate only the static binding files without producing a new migration.
