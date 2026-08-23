using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using GenProgram = Socigy.OpenSource.DB.SourceGenerator.Program;

namespace Socigy.OpenSource.DB.SourceGenerator.Tests;

/// <summary>
/// Runs the incremental generator over an in-memory compilation and hands back both the resulting
/// compilation and the run result (generated sources + diagnostics).
///
/// A static helper rather than a fixture base class on purpose: NUnit runs a base fixture's tests again for
/// every derived fixture, so sharing the harness by inheritance would re-run every generator test once per
/// focused fixture and report a base-class failure several times over.
/// </summary>
internal static class GeneratorTestHarness
{
    public const string LowercaseJson = """{ "database": { "platform": "postgresql", "databaseName": "identity" } }""";

    /// <summary>
    /// Same, but without the ASP.NET Core extensions and connection factory, whose generated code needs
    /// Microsoft.AspNetCore / Microsoft.Extensions.Hosting references the in-memory compilation does not have.
    /// Use this whenever a test inspects <b>bound symbols</b> in the generated output: with those references
    /// missing the trees are still emitted but bind to nothing, so the test silently measures nothing.
    /// </summary>
    public const string NoWebJson = """{ "database": { "platform": "postgresql", "databaseName": "identity", "generateWebAppExtensions": false, "generateDbConnectionFactory": false } }""";

    private sealed class JsonAdditionalText : AdditionalText
    {
        private readonly string _text;
        public override string Path { get; }
        public JsonAdditionalText(string path, string text) { Path = path; _text = text; }
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(_text);
    }

    public static (Compilation Output, GeneratorDriverRunResult Result) Run(string source, string? socigyJson)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);

        // TRUSTED_PLATFORM_ASSEMBLIES rather than AppDomain.CurrentDomain.GetAssemblies(): the latter lists
        // only assemblies already LOADED in the test process, so a reference the generated code needs but the
        // test host has not touched yet (Microsoft.Extensions.DependencyInjection, Logging, …) is simply
        // absent. The generated trees still get added to the compilation, but bind to nothing — which makes
        // any test that inspects bound symbols quietly measure nothing at all.
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && System.IO.File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Socigy.OpenSource.DB.Attributes.TableAttribute).Assembly.Location));
        // The generated code uses Npgsql; without this the generated trees compile to a wall of CS0246 and
        // nothing that inspects bound symbols works.
        references.Add(MetadataReference.CreateFromFile(typeof(Npgsql.NpgsqlConnection).Assembly.Location));

        // Assembly name must NOT start with "Socigy.OpenSource.DB" or the generator self-skips.
        var compilation = CSharpCompilation.Create("SampleModel", new[] { tree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var additional = socigyJson == null
            ? ImmutableArray<AdditionalText>.Empty
            : ImmutableArray.Create<AdditionalText>(new JsonAdditionalText("socigy.json", socigyJson));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new GenProgram().AsSourceGenerator() },
            additionalTexts: additional,
            parseOptions: parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return (output, driver.GetRunResult());
    }
}
