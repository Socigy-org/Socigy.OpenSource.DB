using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;
using Benchmarks;

// Run all benchmarks, or filter, e.g.:
//   dotnet run -c Release -- --filter *QueryBenchmarks*
//   dotnet run -c Release -- --filter *ParseBenchmarks*
//
// Results are written to ./BenchmarkResults/results/ as GitHub-flavoured Markdown, JSON, CSV and HTML.
var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddExporter(MarkdownExporter.GitHub, JsonExporter.Full)
    .WithArtifactsPath("BenchmarkResults");

BenchmarkSwitcher.FromAssembly(typeof(QueryBenchmarks).Assembly).Run(args, config);
