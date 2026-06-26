# Contributing to Socigy.OpenSource.DB

Thanks for your interest in contributing! This guide covers how to build, test, and submit changes.

The project is licensed under **MPL-2.0**. By contributing, you agree your contributions are licensed under the same terms.

---

## Prerequisites

- **.NET SDK 10.0** or newer (`dotnet --version`).
- **PostgreSQL 16** for the database-backed tests — most easily run via Docker (see [Running tests](#running-tests)).
- Optional: an IDE with **T4 / text-template** support (Visual Studio, or `dotnet-t4`) if you change a `*.tt` template — see [Working with templates](#working-with-templates).

---

## Repository layout

| Project | Purpose |
|---------|---------|
| `Socigy.OpenSource.DB.Core` | Runtime library (query builders, context, encryption, diagnostics). `netstandard2.0`, no Npgsql dependency. |
| `Socigy.OpenSource.DB.SourceGenerator` | The Roslyn incremental source generator and its T4 templates. |
| `Socigy.OpenSource.DB.Tool` | The CLI: migration generation and database-first scaffolding. |
| `Socigy.OpenSource.DB` | The NuGet package project — bundles Core, the generator, and the tool. |
| `Socigy.OpenSource.DB.HashiCorp` | Optional Vault integration (field encryption + rotating credentials). |
| `Socigy.OpenSource.DB.UnitTests` | No-database unit tests (Core logic, encryption primitives). |
| `UnitTest.DB` / `UnitTest.DB.Tests` | A real model + its PostgreSQL-backed integration tests. |
| `Socigy.OpenSource.DB.Tool.Tests` | Scaffolding/translation tests + a live-DB introspection round-trip. |
| `Example.*` | Example projects used to validate end-to-end behavior. |
| `Benchmarks` | BenchmarkDotNet suites vs Dapper/EF Core. |

---

## Building

```bash
dotnet build Socigy.OpenSource.DB.slnx
```

To build an individual project, target its `.csproj`. **Note:** `Socigy.OpenSource.DB.Tool` multi-targets `net10.0-windows`, so on Linux/macOS you must pass `EnableWindowsTargeting`:

```bash
dotnet build Socigy.OpenSource.DB.Tool/Socigy.OpenSource.DB.Tool.csproj -p:EnableWindowsTargeting=true
```

The same flag is needed when building/testing anything that references the Tool (e.g. `Socigy.OpenSource.DB.Tool.Tests`).

---

## Running tests

### Start a PostgreSQL instance

The integration tests read their connection string from `UnitTest.DB.Tests/appsettings.json` (key `ConnectionStrings:TestDb:Default`), defaulting to `localhost:5432`, user `postgres`, password `1234`. The quickest way to match it:

```bash
docker run -d --name socigy-pg -e POSTGRES_PASSWORD=1234 -p 5432:5432 postgres:16
```

### Run the suites

```bash
# No database required
dotnet test Socigy.OpenSource.DB.UnitTests/Socigy.OpenSource.DB.UnitTests.csproj

# PostgreSQL required
dotnet test UnitTest.DB.Tests/UnitTest.DB.Tests.csproj

# Scaffolding + introspection (needs the Tool's Windows-targeting flag).
# The introspection test runs against SOCIGY_TEST_PG, else the local default, else self-ignores.
SOCIGY_TEST_PG="Host=localhost;Port=5432;Username=postgres;Password=1234;Database=postgres" \
  dotnet test Socigy.OpenSource.DB.Tool.Tests/Socigy.OpenSource.DB.Tool.Tests.csproj -p:EnableWindowsTargeting=true
```

The test schema is created automatically by `UnitCore` on first run. Tests are written to be order- and parallel-independent (they use unique names/ids), so they can share one database.

CI (`.github/workflows/ci.yml`) runs these against a `postgres:16` service on every push.

---

## Working with templates

The source generator emits code through **T4 text templates** (`*.tt`) under `Socigy.OpenSource.DB.SourceGenerator/Templates/`. These are `TextTemplatingFilePreprocessor` templates: each `*.tt` has a **committed, compiled `*.cs` sibling** that is what actually runs. The build does **not** regenerate the `.cs` automatically.

If you edit a `*.tt`, you must regenerate its `.cs`:

- In Visual Studio: right-click the `.tt` → **Run Custom Tool**, or save the file.
- On the CLI: install and run [`dotnet-t4`](https://www.nuget.org/packages/dotnet-t4).

Commit the `.tt` and its regenerated `.cs` together. A `.tt` change with a stale `.cs` has no effect on the build.

Generator code that does **not** live in a `.tt` (e.g. `TableBindingsGenerator.cs`, `ProcedureGenerator.cs`) is ordinary C# and takes effect on the next build.

---

## Generator diagnostics

Generator diagnostics use the stable `SCGDB###` prefix (category `Socigy.DB`) and are defined in `Socigy.OpenSource.DB.SourceGenerator/Diagnostics.cs`. When adding one:

1. Use the next free id (see the comment at the top of `Diagnostics.cs`) — **never renumber an existing id**.
2. Add a matching row to `AnalyzerReleases.Unshipped.md` (the release-tracking analyzer, RS2000, enforces this).
3. Document it under the **Generator diagnostics** page in the docs.

---

## Versioning and releases

Versions are set in `Directory.Build.props`:

- `<Version>` is the base/assembly version (and the OpenTelemetry instrumentation-scope version).
- `<DbPackageVersion>` and `<HashiCorpPackageVersion>` are the per-package NuGet versions; leave one blank to track `<Version>`.

Bump the relevant version, add a changelog entry, and CI publishes the package(s) whose version isn't already on NuGet.org. The two packages version independently and each gets its own release tag.

---

## Pull requests

1. **Branch** off `master`.
2. Keep changes focused; match the surrounding code's style, naming, and comment density.
3. **Add or update tests.** Database-affecting changes should be covered by `UnitTest.DB.Tests`; generator/CLI changes by the relevant test project.
4. Update the **docs** (the `socigy-docs` site) and the **changelog** when you change public behavior.
5. Make sure the full build and the test suites pass (with a local Postgres running).
6. Open the PR against `master` with a clear description of what changed and why.

If you're planning a larger change, open an issue first to discuss the approach.
