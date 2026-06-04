# Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org/) comparisons of **Socigy.OpenSource.DB** against
**Dapper** and **EF Core** (Npgsql provider), plus a dedicated **NativeAOT** suite.

There are two projects:

| Project | Runtime | Compares |
|---------|---------|----------|
| `Benchmarks` | JIT | Socigy vs **Dapper** vs **EF Core** |
| `Benchmarks.Aot` | JIT **and NativeAOT** | Socigy vs **raw ADO.NET** (Dapper/EF can't run under AOT — see below) |

## JIT suite (`Benchmarks`)

| Suite | DB | What it shows |
|-------|----|----|
| `QueryBenchmarks` | yes | Filtered `SELECT` → objects via the typed `Query(x => …)` API. Socigy / Dapper / EF (tracking + no-tracking). `[Params]` = 1/100/1000 rows. |
| `ProcedureBenchmarks` | yes | The same read expressed as a Socigy **`.sql` procedure** (`Procedures.GetUsersUnderAge`) vs Dapper raw SQL vs EF `FromSqlRaw`. Procedure SQL is fixed at build time, so Socigy does no runtime translation here. |
| `InsertBenchmarks` | yes | Single-row INSERT: Socigy insert builder vs Dapper `ExecuteAsync` vs EF `Add` + `SaveChanges`. |
| `UpdateBenchmarks` | yes | Single-row UPDATE by PK: Socigy update builder vs Dapper `ExecuteAsync` vs EF `ExecuteUpdateAsync`. |
| `ParseBenchmarks` | no | Socigy's per-query LINQ→SQL translation cost in isolation. |

```bash
dotnet run -c Release --project Benchmarks                                   # all
dotnet run -c Release --project Benchmarks -- --filter *ProcedureBenchmarks*
dotnet run -c Release --project Benchmarks -- --filter *InsertBenchmarks*
dotnet run -c Release --project Benchmarks -- --filter *UpdateBenchmarks*
dotnet run -c Release --project Benchmarks -- --filter *ParseBenchmarks*      # no DB
```

## NativeAOT suite (`Benchmarks.Aot`)

Runs `QueryBenchmarks`, `ProcedureBenchmarks` and `InsertBenchmarks` under **both JIT and NativeAOT**
(side-by-side in one summary) so you can see the AOT story on paper.

It compares **Socigy vs hand-written ADO.NET** only — on purpose:

> **Dapper and EF Core are not NativeAOT-compatible.** Dapper materializes rows with
> `System.Reflection.Emit` (runtime IL generation) and EF Core's query pipeline relies on runtime
> codegen; both throw under NativeAOT. Raw ADO.NET is the AOT-safe floor, and Socigy's **generated
> `ConvertFrom`** needs no reflection/codegen, which is exactly what makes it AOT-friendly.

```bash
dotnet run -c Release --project Benchmarks.Aot
```

### NativeAOT prerequisites
The NativeAOT job publishes a native build per benchmark, so it needs:
- The **NativeAOT toolchain** — BenchmarkDotNet pulls `Microsoft.DotNet.ILCompiler` (10.0.0) from NuGet.
- A **C/C++ toolchain/linker**: on Windows, the *Desktop development with C++* workload (or Build Tools)
  for `link.exe`; on Linux, `clang` + `zlib`. See the
  [.NET NativeAOT prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/#prerequisites).

The project also sets `IsAotCompatible=true`, so a plain `dotnet build` surfaces any trim/AOT analyzer
warnings (e.g. the `Expression.Compile()` fallback used only for complex predicates — the common
predicate path reads captured values without code generation).

## Setup
A live PostgreSQL is required for everything except `ParseBenchmarks`. Point at it with `BENCH_DB`
(defaults to `Host=localhost;Port=5432;Username=postgres;Password=1234;Database=postgres`). The
`bench_users` (1000 rows) and `bench_writes` tables are created/seeded automatically on first run.

## Reading the results fairly
- **Socigy** translates the LINQ predicate to parameterized SQL per call (`ParseBenchmarks`) and
  materializes via generated `ConvertFrom`. Procedures skip the translation step entirely.
- **Dapper** gets hand-written SQL (no translation) and maps columns by name
  (`MatchNamesWithUnderscores` on).
- **EF Core** caches query compilation; `AsNoTracking` is the closest comparison to Socigy/Dapper.
- Each method opens a pooled connection (or fresh `DbContext`) per invocation — a realistic
  per-request pattern, but it means connection acquisition is part of the baseline and compresses the
  relative gaps.
