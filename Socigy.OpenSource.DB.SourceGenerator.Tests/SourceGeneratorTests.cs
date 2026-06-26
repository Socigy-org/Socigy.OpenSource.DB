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
/// Runs the incremental generator over in-memory compilations to cover the praxe_app fixes:
/// #1 (no-op without socigy.json), #2 (required members), #4 (identifier casing / contextName).
/// </summary>
[TestFixture]
public class SourceGeneratorTests
{
    private const string LowercaseJson = """{ "database": { "platform": "postgresql", "databaseName": "identity" } }""";

    private static string Model(string body = "") => $$"""
        using System;
        using Socigy.OpenSource.DB.Attributes;
        namespace Sample
        {
            [Table("users")]
            public partial class User
            {
                [PrimaryKey] public Guid Id { get; set; }
                {{body}}
            }
        }
        """;

    private sealed class JsonAdditionalText : AdditionalText
    {
        private readonly string _text;
        public override string Path { get; }
        public JsonAdditionalText(string path, string text) { Path = path; _text = text; }
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(_text);
    }

    private static (Compilation Output, GeneratorDriverRunResult Result) Run(string source, string? socigyJson)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Socigy.OpenSource.DB.Attributes.TableAttribute).Assembly.Location));

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

    private static string AllGenerated(GeneratorDriverRunResult result) =>
        string.Join("\n", result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString()));

    // ── #4: ToTypeIdentifier helper ─────────────────────────────────────────────
    [TestCase("identity", "Identity")]
    [TestCase("IdentityDb", "IdentityDb")]
    [TestCase("MyDb", "MyDb")]
    [TestCase("my-db", "Mydb")]
    [TestCase("2fa", "_2fa")]
    [TestCase("", "UnnamedDb")]
    public void ToTypeIdentifier_produces_valid_identifiers(string input, string expected)
        => Assert.That(GenProgram.ToTypeIdentifier(input), Is.EqualTo(expected));

    // ── #1: no socigy.json -> no exception, no context output ───────────────────
    [Test]
    public void No_socigy_json_does_not_throw_and_emits_no_context()
    {
        var (_, result) = Run(Model(), socigyJson: null);

        Assert.That(result.Results.All(r => r.Exception == null), "the generator must not throw without socigy.json");
        Assert.That(AllGenerated(result), Does.Not.Contain("public interface I"), "no context interface without a platform");
    }

    // ── #4: lowercase databaseName -> clean identifiers, raw service key ─────────
    [Test]
    public void Lowercase_databaseName_yields_valid_identifiers_and_raw_service_key()
    {
        var (_, result) = Run(Model(), LowercaseJson);
        string gen = AllGenerated(result);

        Assert.That(result.Results.All(r => r.Exception == null));
        Assert.That(gen, Does.Contain("interface IIdentity"), "lowercase 'identity' should produce IIdentity, not Iidentity");
        Assert.That(gen, Does.Contain("AddIdentity"), "DI method should be AddIdentity()");
        Assert.That(gen, Does.Not.Contain("interface Iidentity"));
        Assert.That(gen, Does.Contain("\"identity\""), "the keyed DI service / connection-string key stays the raw 'identity'");
    }

    [Test]
    public void ContextName_overrides_the_generated_identifier()
    {
        var json = """{ "database": { "platform": "postgresql", "databaseName": "identity", "contextName": "IdentityDb" } }""";
        string gen = AllGenerated(Run(Model(), json).Result);

        Assert.That(gen, Does.Contain("interface IIdentityDb"));
        Assert.That(gen, Does.Contain("AddIdentityDb"));
        Assert.That(gen, Does.Contain("\"identity\""), "physical/service key still the raw databaseName");
    }

    // ── #2: required members ────────────────────────────────────────────────────
    [Test]
    public void Required_members_compile_and_get_SetsRequiredMembers()
    {
        var (output, result) = Run(Model("public required string Email { get; set; }"), LowercaseJson);
        string gen = AllGenerated(result);

        Assert.That(gen, Does.Contain("[global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]"));
        // The new() constraint must be satisfied — no CS9040 / CS9035 about required members.
        var requiredErrors = output.GetDiagnostics().Where(d => d.Id is "CS9040" or "CS9035").ToList();
        Assert.That(requiredErrors, Is.Empty, "required members must not break the new() constraint:\n" +
            string.Join("\n", requiredErrors.Select(d => d.ToString())));
    }
}
