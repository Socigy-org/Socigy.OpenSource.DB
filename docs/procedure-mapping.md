# Procedure Mapping

Procedure mapping lets you write raw SQL in `.sql` files and have the source generator produce type-safe C# wrapper methods automatically. Parameters and return types are declared through header comments; no hand-written boilerplate is needed.

## Directory convention

Place `.sql` files anywhere under `Socigy/Procedures/` inside your project:

```
YourProject/
├── Socigy/
│   └── Procedures/
│       ├── InsertAuditLog.sql          → Procedures.InsertAuditLog(conn, ...)
│       └── Reports/
│           └── GetTopUsers.sql         → Procedures.Reports.GetTopUsers(conn, ...)
└── YourProject.csproj
```

Sub-directory segments become nested static classes in the generated code. The file name (without extension) becomes the method name.

### Registering files as AdditionalFiles

Declare the SQL files as `AdditionalFiles` in your `.csproj` so the source generator can read them:

```xml
<ItemGroup>
    <AdditionalFiles Include="Socigy\Procedures\**\*.sql" />
</ItemGroup>
```

This glob includes all `.sql` files in the tree recursively.

## SQL file format

Every `.sql` file may start with a header block of `--` comment lines that declare metadata. Any line that does not start with `--` ends the header; the remaining content is the SQL body.

```sql
-- @returns: MyNamespace.MyReturnType
-- @param paramName: CSharpType
-- (additional non-directive comments are silently ignored)

SELECT ...
```

### `@returns`

Declares the C# type that each row maps to. The type must be a `[Table]` class with a generated `static ConvertFrom(DbDataReader)` method (all generated table classes have this automatically).

```sql
-- @returns: MyApp.Models.User
SELECT "id", "username", "created_at" FROM "users" WHERE "id" = @id
```

Omit `@returns` entirely for void procedures (INSERT / UPDATE / DELETE / CALL):

```sql
-- @param userId: System.Guid
-- @param label: string
INSERT INTO "audit_log" ("user_id", "label") VALUES (@userId, @label)
```

### `@param`

Declares a method parameter. Each `@param` line contributes one argument to the generated method, in the order they appear:

```
-- @param name: CSharpType
```

- `name` — the C# parameter name (also used as the SQL parameter name `@name`)
- `CSharpType` — any valid C# type expression (`System.Guid`, `string`, `int`, `MyApp.Models.Status`, etc.)

## Generated code

Given the file structure above, the generator emits:

```csharp
// namespace: YourProject.Socigy.Generated
namespace YourProject.Socigy.Generated
{
    public static partial class Procedures
    {
        // From Socigy/Procedures/InsertAuditLog.sql (void — no @returns)
        public static async System.Threading.Tasks.Task<bool> InsertAuditLog(
            DbConnection conn,
            System.Guid userId,
            string label)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO ""audit_log"" (""user_id"", ""label"") VALUES (@userId, @label)";
            var __p0 = cmd.CreateParameter();
            __p0.ParameterName = "@userId";
            __p0.Value = (object?)userId ?? System.DBNull.Value;
            cmd.Parameters.Add(__p0);
            var __p1 = cmd.CreateParameter();
            __p1.ParameterName = "@label";
            __p1.Value = (object?)label ?? System.DBNull.Value;
            cmd.Parameters.Add(__p1);
            int affected = await cmd.ExecuteNonQueryAsync();
            return affected >= 0;
        }

        public static partial class Reports
        {
            // From Socigy/Procedures/Reports/GetTopUsers.sql (returns rows)
            public static async System.Collections.Generic.IAsyncEnumerable<MyApp.Models.User> GetTopUsers(
                DbConnection conn,
                int limit,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                System.Threading.CancellationToken cancellationToken = default)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT ...";
                var __p0 = cmd.CreateParameter();
                __p0.ParameterName = "@limit";
                __p0.Value = (object?)limit ?? System.DBNull.Value;
                cmd.Parameters.Add(__p0);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    yield return MyApp.Models.User.ConvertFrom(reader);
            }
        }
    }
}
```

The generated namespace is `{AssemblyName}.Socigy.Generated`. Nested classes mirror the sub-directory tree.

## Calling generated procedures

### Void procedure

A procedure without `@returns` generates a `Task<bool>` method. The return value is `true` when `ExecuteNonQueryAsync()` returns `>= 0` (which is always the case for successful commands):

```csharp
using YourProject.Socigy.Generated;

await using var conn = dataSource.CreateConnection();
await conn.OpenAsync();

bool ok = await Procedures.InsertAuditLog(conn, userId, "login");
```

### Return-type procedure

A procedure with `@returns: SomeType` generates an `async IAsyncEnumerable<SomeType>` method. Iterate with `await foreach` or collect with `ToListAsync()`:

```csharp
// Stream rows
await foreach (var user in Procedures.Reports.GetTopUsers(conn, limit: 10))
{
    Console.WriteLine(user.Username);
}

// Collect to list (requires System.Linq.Async)
var users = await Procedures.Reports.GetTopUsers(conn, limit: 10).ToListAsync();
```

Cancellation is supported via an optional `CancellationToken` parameter that is always the last argument:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await foreach (var row in Procedures.Reports.GetTopUsers(conn, 50, cts.Token))
    Process(row);
```

## Complete example

**`Socigy/Procedures/Users/GetByUsername.sql`**

```sql
-- @returns: MyApp.Models.User
-- @param username: string
SELECT "id", "username", "email", "created_at"
FROM "users"
WHERE "username" = @username
LIMIT 1
```

**`Socigy/Procedures/Users/Deactivate.sql`**

```sql
-- @param userId: System.Guid
-- @param deactivatedAt: System.DateTime
UPDATE "users"
SET "is_active" = FALSE, "deactivated_at" = @deactivatedAt
WHERE "id" = @userId
```

**Usage:**

```csharp
using MyApp.Socigy.Generated;

// Find a user
User? found = null;
await foreach (var u in Procedures.Users.GetByUsername(conn, "alice"))
    found = u;

// Deactivate
if (found != null)
    await Procedures.Users.Deactivate(conn, found.Id, DateTime.UtcNow);
```

## Return-type requirements

The C# type named in `@returns` must expose a `static ConvertFrom(DbDataReader reader, Dictionary<string, string>? columnOverrides = null)` method. All table classes generated by Socigy have this method automatically. If you need to return a custom projection type, you must implement the method yourself:

```csharp
public class UserSummary
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";

    public static UserSummary ConvertFrom(
        System.Data.Common.DbDataReader reader,
        System.Collections.Generic.Dictionary<string, string>? _ = null)
    {
        return new UserSummary
        {
            Id       = reader.GetGuid(reader.GetOrdinal("id")),
            Username = reader.GetString(reader.GetOrdinal("username"))
        };
    }
}
```

## Naming rules

| Source | Generated identifier |
|--------|----------------------|
| `GetById.sql` | `GetById` |
| `get-by-id.sql` | `get_by_id` (hyphens become underscores) |
| `Users/` directory | `public static partial class Users` |
| `UserReports/` | `public static partial class UserReports` |

Any character that is not a letter, digit, or underscore is replaced by `_`. A leading digit is prefixed with `_`.

## See also

- [Querying](querying.md) — lambda-based query builder for single-table reads
- [Joins and Set Operations](joins-and-set-operations.md) — multi-table queries without raw SQL
- [CRUD Operations](crud-operations.md) — generated insert / update / delete builders
