using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Benchmarks.Aot;

// Runs the AOT-viable benchmarks under both the JIT and the NativeAOT runtimes so the summary shows
// JIT vs AOT side by side. Using the NativeAot net10 runtime moniker lets BenchmarkDotNet pick the
// ILCompiler version that matches the installed SDK (pinning it explicitly causes a runtime-pack
// mismatch — "PrivateSdkAssemblies ItemGroup is required").
// Results are written to ./BenchmarkResults/results/ as GitHub-flavoured Markdown, JSON, CSV and HTML.
var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(Job.Default.WithRuntime(CoreRuntime.Core10_0).WithId("JIT"))
    .AddJob(Job.Default.WithRuntime(NativeAotRuntime.Net10_0).WithId("NativeAOT"))
    .AddExporter(MarkdownExporter.GitHub, JsonExporter.Full)
    .WithArtifactsPath("BenchmarkResults");

BenchmarkRunner.Run(
    new[] { typeof(AotQueryBenchmarks), typeof(AotProcedureBenchmarks), typeof(AotInsertBenchmarks) },
    config);
