<div align="center">

# Socigy.OpenSource.DB

**A compile-time, AOT-friendly, multi-engine SQL data layer for .NET - zero boilerplate, fully typed.**

A Roslyn incremental source generator that reads your annotated C# classes at build time and emits a fully typed data layer - INSERT, SELECT, UPDATE, DELETE, JOINs, set operations, and migrations - without a single line of boilerplate. The engine is pluggable; **PostgreSQL** is the currently supported target, with other engines on the way.

[![NuGet](https://img.shields.io/nuget/v/Socigy.OpenSource.DB?logo=nuget&label=NuGet)](https://nuget.org/packages/Socigy.OpenSource.DB)
[![Downloads](https://img.shields.io/nuget/dt/Socigy.OpenSource.DB?logo=nuget&label=Downloads)](https://nuget.org/packages/Socigy.OpenSource.DB)
[![CI](https://img.shields.io/github/actions/workflow/status/Socigy-org/Socigy.OpenSource.DB/ci.yml?branch=master&logo=github&label=CI)](https://github.com/Socigy-org/Socigy.OpenSource.DB/actions/workflows/ci.yml)
[![Stars](https://img.shields.io/github/stars/Socigy-org/Socigy.OpenSource.DB?logo=github)](https://github.com/Socigy-org/Socigy.OpenSource.DB)
<br/>
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-supported-336791?logo=postgresql&logoColor=white)](https://www.postgresql.org/)
[![AOT](https://img.shields.io/badge/Native%20AOT-compatible-success)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![License](https://img.shields.io/badge/license-MPL--2.0-blue)](LICENSE)
[![Docs](https://img.shields.io/badge/docs-socigy.com%2Fdatabase-2ea44f)](https://docs.socigy.com/database/)

**[Full documentation → docs.socigy.com/database](https://docs.socigy.com/database/)**

</div>

---

## Installation

```bash
dotnet add package Socigy.OpenSource.DB
```

A single package reference installs the Core runtime, the Roslyn source generator, and the CLI migration tool.

### Packages

| Package                                                                                                                                                                                            | Description                                                                         |
| -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------- |
| [![Socigy.OpenSource.DB](https://img.shields.io/nuget/v/Socigy.OpenSource.DB?label=Socigy.OpenSource.DB)](https://nuget.org/packages/Socigy.OpenSource.DB)                                         | Core runtime, source generator, and CLI migration tool                              |
| [![Socigy.OpenSource.DB.HashiCorp](https://img.shields.io/nuget/v/Socigy.OpenSource.DB.HashiCorp?label=Socigy.OpenSource.DB.HashiCorp)](https://nuget.org/packages/Socigy.OpenSource.DB.HashiCorp) | Optional HashiCorp Vault / OpenBao integration - field encryption and rotating DB credentials |

```bash
# Optional Vault integration
dotnet add package Socigy.OpenSource.DB.HashiCorp
```

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

**2. Build - the generator emits all query methods**

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

- **Zero boilerplate** - annotate once, every CRUD method is generated at build time
- **Fully typed** - WHERE clauses, ORDER BY, and field selectors use C# expressions; no raw strings
- **Migrations** - CLI tool analyses your compiled assembly and generates PostgreSQL DDL; a tracking table handles incremental applies
- **JOINs** - `Join`, `LeftJoin`, `RightJoin`, `FullOuterJoin`, `NaturalJoin`, `CrossJoin`
- **Set operations** - `Union`, `UnionAll`, `Intersect`, `IntersectAll`, `Except`, `ExceptAll`
- **Flagged enums** - `[FlaggedEnum]` generates a junction table and typed flag helpers
- **JSON columns** - `[JsonColumn]` and `[RawJsonColumn]` for JSONB with optional AOT-safe typed serialisation
- **Procedure mapping** - write SQL in `.sql` files, get strongly-typed async wrappers at compile time
- **Value convertors** - custom per-column read/write transformation via `IDbValueConvertor<T>`
- **Field encryption** - `[Encrypted]` columns with pluggable encryptors, including optional [HashiCorp Vault](https://www.vaultproject.io/) / [OpenBao](https://openbao.org/) (KV-v2 keyring, Transit/envelope encryption) and rotating DB credentials
- **Observability** - built-in OpenTelemetry instrumentation (`SocigyDbInstrumentation`) for queries and Vault token lifecycle
- **AOT compatible** - no runtime reflection; safe to publish with `PublishAot=true`

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

`databaseName` is also the connection-string key and the physical database name. To keep a lowercase,
Postgres-conventional name (e.g. `"identity"`) while generating clean C# identifiers, add an optional
`"contextName": "IdentityDb"` — the generated surface becomes `IIdentityDb` / `AddIdentityDb()` while the
connection-string key and physical database stay `identity`.

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

## Field encryption

Mark a column `[Encrypted]` to store it as `bytea`, encrypted on write and decrypted on read by the
ambient `IFieldEncryptor`. The ciphertext is authenticated and bound to its `table:column` context, so a
value cannot be relocated to another column and still decrypt.

```csharp
[Table("users")]
public partial class User
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)] public Guid Id { get; set; }
    [Encrypted] public string Ssn { get; set; }
}
```

**Local key** — configure once at startup with a 32-byte key from your secret store:

```csharp
SocigyFieldEncryption.Configure(new AesFieldEncryptor(key)); // AES-256-CBC + HMAC-SHA256
```

**HashiCorp Vault / OpenBao** (`Socigy.OpenSource.DB.HashiCorp`) offers three modes. The envelope and
EaaS modes use the Transit engine — enable it and create a key first:

```bash
vault secrets enable transit
vault write -f transit/keys/socigy-db                 # envelope mode (non-derived)
vault write transit/keys/socigy-eaas derived=true     # EaaS mode (binds the table:column context)
```

```csharp
// 1) KV-direct — key lives in a Vault KV-v2 secret, loaded once; crypto is local.
builder.Services.AddSocigyVaultEncryption(o => { o.Address = "https://vault:8200"; o.Token = "…"; });

// 2) Data-key envelope (recommended) — a versioned keyring of Transit-wrapped DEKs. Crypto stays
//    local; old rows stay readable across rotations because each value embeds its key id.
builder.Services.AddSocigyVaultEnvelopeEncryption(o =>
{
    o.Address = "https://vault:8200"; o.AppRoleId = "…"; o.AppRoleSecretId = "…";
    o.TransitKeyName = "socigy-db";
    o.EnableBackgroundRotation = true;          // optional; or call RotateAsync() manually
    o.RotationInterval = TimeSpan.FromDays(90); // optional; default 90 days
});

// 3) EaaS-direct — Vault encrypts/decrypts each field (a round-trip per field). For a few
//    highly-sensitive columns only. Uses the derived key so the table:column context binds.
builder.Services.AddSocigyVaultTransitEncryption(o =>
{
    o.Address = "https://vault:8200"; o.Token = "…";
    o.TransitKeyName = "socigy-eaas";   // the derived key created above
    o.Profile = "transit";              // route only [Encrypted(Profile = "transit")] columns here
});
```

> **OpenBao** is supported as a drop-in for HashiCorp Vault — point the same options at your OpenBao
> address (its KV-v2 and Transit APIs are wire-compatible). The integration test suite passes against both.

**Activate before any data work.** `AddSocigyVault*Encryption` only *registers* the encryptors; they are
primed from Vault at host start. Anything that touches an `[Encrypted]` column before `Run()` — notably the
migration call in the quickstart above — needs encryption activated first:

```csharp
var app = builder.Build();

await app.UseSocigyVaultEncryption();     // primes + activates every registered profile
await app.EnsureLatestMyDbMigration();    // safe now: [Encrypted] columns are usable

app.Run();
```

It is idempotent, so the startup priming simply finds the work already done. Not needed for
`AddSocigyAesEncryption`, whose key is available synchronously at registration.

**Per-column profiles** — run one mode by default and route specific columns to another:

```csharp
[Encrypted] public string Email { get; set; }                    // default encryptor (e.g. envelope)
[Encrypted(Profile = "transit")] public string Ssn { get; set; }  // EaaS-direct
```

Check readiness with `SocigyFieldEncryption.IsProfileConfigured("transit")` (`IsConfigured` covers only the
default profile).

**Key rotation** — with envelope mode old rows stay readable after a rotation, and any row your app
re-saves migrates to the new key automatically. To proactively rewrite old rows (e.g. to retire a key
version), use the bulk re-encryptor — it works for generated, dynamic, and `[TableType]` tables:

```csharp
await new FieldReencryptor()
    .Add<User>()
    .AddDynamic<Event>("events_2026_06")     // dynamic / [TableType] tables bound to a runtime name
    .RunAsync(connection);                    // batched, resumable; DryRun/Force via ReencryptOptions
```

---

## Documentation

Full reference covering every attribute, builder method, join variant, migration option, and DI pattern:

**[docs.socigy.com/database](https://docs.socigy.com/database/)**

| Section                                                                              | Topics                                                   |
| ------------------------------------------------------------------------------------ | -------------------------------------------------------- |
| [Getting started](https://docs.socigy.com/database/0.3.3/getting-started/quickstart) | Installation, project structure, `socigy.json`           |
| [Defining models](https://docs.socigy.com/database/0.3.3/defining-models/tables)     | All attributes, column types, defaults, constraints      |
| [Querying](https://docs.socigy.com/database/0.3.3/querying/select)                   | SELECT, INSERT, UPDATE, DELETE, JOINs, set operations    |
| [Migrations](https://docs.socigy.com/database/0.3.3/migration/cli-tool)              | CLI tool, schema generation, applying, custom migrations |
| [Advanced](https://docs.socigy.com/database/0.3.3/advanced/procedure-mapping)        | Procedure mapping, value convertors, Check DSL           |

---

## License

Mozilla Public License 2.0 (MPL-2.0) - see [LICENSE](LICENSE).
