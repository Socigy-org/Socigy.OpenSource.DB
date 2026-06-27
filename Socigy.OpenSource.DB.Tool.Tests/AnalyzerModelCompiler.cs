using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Socigy.OpenSource.DB.Tool;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// Compiles a small <c>[Table]</c> model source string to a real assembly and runs it through
/// <see cref="AssemblyAnalyzer.LoadAndAnalyze"/> — the only way to exercise the reflection-based forward analysis
/// (attribute reading under MetadataLoadContext) end to end. The DLL is written into the test base directory so the
/// analyzer's reference resolver (which scans that dir + the runtime) can bind the attribute assembly.
/// </summary>
internal static class AnalyzerModelCompiler
{
    public static DbSchema Analyze(string modelSource)
    {
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Select(p => MetadataReference.CreateFromFile(p))
            .ToList();

        string asmName = "AnalyzerFixture_" + Guid.NewGuid().ToString("N");
        var compilation = CSharpCompilation.Create(
            asmName,
            new[] { CSharpSyntaxTree.ParseText(modelSource) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        string dllPath = Path.Combine(AppContext.BaseDirectory, asmName + ".dll");
        var emit = compilation.Emit(dllPath);
        if (!emit.Success)
        {
            string errors = string.Join("\n", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            throw new InvalidOperationException("Fixture model failed to compile:\n" + errors);
        }

        // Initialize the tool config (platform = postgresql) in an isolated temp dir so the analyzer's
        // GetSqlGenerator() resolves; no socigy.json there means defaults are used and nothing touches the repo.
        string projectDir = Path.Combine(Path.GetTempPath(), "socigy-analyzer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        Configuration.InitializeAsync(projectDir, new FileInfo(dllPath)).GetAwaiter().GetResult();

        return AssemblyAnalyzer.LoadAndAnalyze(new FileInfo(dllPath));
    }
}
