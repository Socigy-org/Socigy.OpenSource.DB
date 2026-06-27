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

    // ── [Encrypted] + [StringLength] is contradictory (bytea vs varchar(n)) and was order-dependent in the
    //    migration DDL; it must now be a blocking SCGDB002 error. ─────────────────────────────────────────────
    [Test]
    public void Encrypted_with_StringLength_reports_SCGDB002()
    {
        var (_, result) = Run(Model("[Encrypted, StringLength(50)] public string Secret { get; set; }"), LowercaseJson);
        var combo = result.Diagnostics.Where(d => d.Id == "SCGDB002").ToList();
        Assert.That(combo, Is.Not.Empty, "[Encrypted] combined with [StringLength] must report SCGDB002");
    }

    [Test]
    public void Encrypted_without_StringLength_is_fine()
    {
        var (_, result) = Run(Model("[Encrypted] public string Secret { get; set; }"), LowercaseJson);
        var combo = result.Diagnostics.Where(d => d.Id == "SCGDB002").ToList();
        Assert.That(combo, Is.Empty, "[Encrypted] alone must not report the combo error");
    }

    // The baked [TableType] CREATE TABLE must map an `object` column to jsonb (matching the migration generator),
    // not fall through to text — so a runtime-instantiated table and a migration-managed one agree.
    [Test]
    public void TableType_ObjectColumn_BakedAsJsonb()
    {
        const string source = """
            using System;
            using Socigy.OpenSource.DB.Attributes;
            namespace Sample
            {
                [TableType]
                public partial class Doc
                {
                    [PrimaryKey] public Guid Id { get; set; }
                    public object Payload { get; set; }
                }
            }
            """;
        var (_, result) = Run(source, LowercaseJson);
        string gen = AllGenerated(result);

        Assert.That(gen, Does.Contain("GetCreateTableSql"), "a [TableType] emits a baked CREATE TABLE");
        Assert.That(gen, Does.Contain("jsonb"), "the object column must be baked as jsonb, not text");
    }

    // Regression: two [Table] classes with the same simple name in different namespaces produced identical hint
    // names and crashed the whole generator. They must each generate under a namespace-qualified hint name.
    [Test]
    public void SameSimpleName_DifferentNamespaces_BothGenerate()
    {
        const string source = """
            using System;
            using Socigy.OpenSource.DB.Attributes;
            namespace Auth { [Table("auth_users")] public partial class User { [PrimaryKey] public Guid Id { get; set; } } }
            namespace Billing { [Table("billing_users")] public partial class User { [PrimaryKey] public Guid Id { get; set; } } }
            """;
        var (_, result) = Run(source, LowercaseJson);
        string gen = AllGenerated(result);

        Assert.That(gen, Does.Contain("auth_users"), "the Auth.User table must generate");
        Assert.That(gen, Does.Contain("billing_users"), "the Billing.User table must generate (no hint-name crash)");
    }

    // Regression: a pure [TableType] (no [Table]) is a runtime-named row shape and need not have a primary key, so
    // it must NOT emit the SCGDB016 no-primary-key warning.
    [Test]
    public void TableType_WithoutPrimaryKey_DoesNotWarnNoPk()
    {
        const string source = """
            using Socigy.OpenSource.DB.Attributes;
            namespace Sample { [TableType] public partial class Projection { public string Name { get; set; } public int Count { get; set; } } }
            """;
        var (_, result) = Run(source, LowercaseJson);
        Assert.That(result.Diagnostics.Where(d => d.Id == "SCGDB016"), Is.Empty,
            "a pure [TableType] must not warn about a missing primary key");
    }

    // A generic [Table] would emit an uncompilable partial; it must instead report SCGDB025.
    [Test]
    public void Generic_Table_ReportsSCGDB025()
    {
        const string source = """
            using System;
            using Socigy.OpenSource.DB.Attributes;
            namespace Sample { [Table("g")] public partial class Generic<T> { [PrimaryKey] public Guid Id { get; set; } } }
            """;
        var (_, result) = Run(source, LowercaseJson);
        Assert.That(result.Diagnostics.Where(d => d.Id == "SCGDB025"), Is.Not.Empty,
            "a generic [Table] must report SCGDB025, not emit broken code");
    }
}
