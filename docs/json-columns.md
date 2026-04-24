# JSON Columns

Socigy.OpenSource.DB supports storing structured JSON data in `jsonb` PostgreSQL columns through two attributes: `[RawJsonColumn]` for plain string storage and `[JsonColumn]` for typed, AOT-safe serialization.

## `[RawJsonColumn]`

Use `[RawJsonColumn]` when you want to store an arbitrary JSON string verbatim. The property must be `string?`.

```csharp
[Table("events")]
public partial class Event
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Any valid JSON — stored as-is in a jsonb column.</summary>
    [RawJsonColumn]
    public string? Metadata { get; set; }
}
```

**What the migration generates:**

```sql
CREATE TABLE "events" (
    "id"       UUID  NOT NULL DEFAULT gen_random_uuid(),
    "name"     TEXT  NOT NULL,
    "metadata" JSONB,
    PRIMARY KEY ("id")
);
```

**Insert and query:**

```csharp
var ev = new Event
{
    Name = "UserSignup",
    Metadata = """{"ip":"192.168.1.1","agent":"Mozilla/5.0"}"""
};
await ev.Insert().WithConnection(conn).ExecuteAsync();

// Read back — PostgreSQL normalises JSONB whitespace
await foreach (var e in Event.Query(x => x.Id == ev.Id).WithConnection(conn).ExecuteAsync())
{
    using var doc = JsonDocument.Parse(e.Metadata!);
    var ip = doc.RootElement.GetProperty("ip").GetString();
}
```

## `[JsonColumn(typeof(TContext))]`

Use `[JsonColumn]` when you have a strongly-typed payload class and want AOT-safe serialization via a `JsonSerializerContext`. The serializer context is the same pattern required by `System.Text.Json` source generation.

### Step 1 — define the payload class

```csharp
public class UserProfile
{
    public string Bio { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public List<string> Interests { get; set; } = [];
}
```

### Step 2 — create an AOT-safe serializer context

```csharp
[JsonSerializable(typeof(UserProfile))]
public partial class MyDbJsonContext : JsonSerializerContext { }
```

Place this in the same assembly as your table classes. One context can cover multiple payload types:

```csharp
[JsonSerializable(typeof(UserProfile))]
[JsonSerializable(typeof(NotificationSettings))]
[JsonSerializable(typeof(OAuthTokens))]
public partial class MyDbJsonContext : JsonSerializerContext { }
```

### Step 3 — annotate the table property

```csharp
[Table("users")]
public partial class User
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    public string Email { get; set; } = "";

    [JsonColumn(typeof(MyDbJsonContext))]
    public UserProfile? Profile { get; set; }
}
```

**What the migration generates:**

```sql
CREATE TABLE "users" (
    "id"      UUID  NOT NULL DEFAULT gen_random_uuid(),
    "email"   TEXT  NOT NULL,
    "profile" JSONB,
    PRIMARY KEY ("id")
);
```

**Insert, query, and update:**

```csharp
// Insert
var user = new User
{
    Email = "alice@example.com",
    Profile = new UserProfile
    {
        Bio = "Software engineer",
        AvatarUrl = "https://cdn.example.com/alice.jpg",
        Interests = ["code", "coffee", "cats"]
    }
};
await user.Insert().WithConnection(conn).ExecuteAsync();

// Query — Profile is automatically deserialized
await foreach (var u in User.Query(x => x.Id == user.Id).WithConnection(conn).ExecuteAsync())
{
    Console.WriteLine(u.Profile?.Bio);          // "Software engineer"
    Console.WriteLine(u.Profile?.Interests[0]); // "code"
}

// Update
user.Profile = new UserProfile { Bio = "Principal engineer", AvatarUrl = user.Profile!.AvatarUrl };
await user.Update().WithConnection(conn).WithAllFields().ExecuteAsync();

// Set to null
user.Profile = null;
await user.Update().WithConnection(conn).WithFields(x => new object?[] { x.Profile }).ExecuteAsync();
```

## How it works

### Serialization path (INSERT / UPDATE)

When a `[JsonColumn]` property is non-null, `GetColumns()` serializes it to a JSON string using the provided context before handing it to the command builder:

```
Payload = new UserProfile { ... }
    → JsonSerializer.Serialize(value, MyDbJsonContext.Default.Options)
    → "{\"bio\":\"...\",\"avatarUrl\":\"...\",\"interests\":[...]}"
    → NpgsqlParameter with NpgsqlDbType.Jsonb
```

A `[RawJsonColumn]` property is passed through unchanged — the string you assign is the string stored in the database.

For both types the command builder sets `NpgsqlDbType.Jsonb` on the parameter so Npgsql validates and stores the value as `jsonb`.

### Deserialization path (SELECT)

When `ConvertFrom(DbDataReader reader)` runs for a typed JSON column, the generator emits:

```csharp
var __json = ReadValue<string?>(reader, PayloadColumnName, columnOverrides);
Profile = __json == null ? null
    : JsonSerializer.Deserialize<UserProfile>(__json, MyDbJsonContext.Default.Options);
```

For a `[RawJsonColumn]` property, `ReadValue<string?>` is used directly — the raw JSON string is returned as-is from PostgreSQL.

### Migration schema (`structure.json`)

The migration tool records both the resolved DB type and the JSON flags in `structure.json`. A column annotated with either JSON attribute will appear as:

```json
{
  "name": "profile",
  "dotnetType": "MyNamespace.UserProfile",
  "databaseType": "jsonb",
  "isJsonColumn": true,
  "jsonContextType": "MyNamespace.MyDbJsonContext"
}
```

A `[RawJsonColumn]` column omits `jsonContextType`:

```json
{
  "name": "metadata",
  "dotnetType": "System.String",
  "databaseType": "jsonb",
  "isJsonColumn": true
}
```

This ensures that when the migration tool calculates a diff, it correctly identifies the column as `jsonb` and does not attempt to re-migrate it as `text`.

## `ColumnInfo` metadata

Both JSON column types expose `IsJson = true` through the generated `GetColumns()` method, which lets you inspect column metadata at runtime:

```csharp
var cols = new User().GetColumns();

cols["profile"].IsJson   // true
cols["profile"].Type     // typeof(string)  ← the DB representation type
cols["email"].IsJson     // false
```

The `Type` for a JSON column is always `typeof(string)` because the value handed to the database is a serialized string regardless of the CLR property type.

## Querying JSON fields

The WHERE visitor does not currently parse into JSON sub-fields. Filter on regular columns and perform JSON field access in application code after the query returns:

```csharp
// Fetch all users, then filter in memory
await foreach (var u in User.Query().WithConnection(conn).ExecuteAsync())
{
    if (u.Profile?.Interests.Contains("coffee") == true)
        Console.WriteLine(u.Email);
}

// Or add a companion indexed column for fields you need to filter on
```

## AOT compatibility

Both attributes are AOT-safe. `[RawJsonColumn]` performs no reflection. `[JsonColumn]` relies solely on the provided `JsonSerializerContext`, which is source-generated and requires no runtime reflection.

Ensure the context is registered before any serialization happens — this is automatic because the source generator emits the call inline in the generated partial class rather than in a static constructor.
