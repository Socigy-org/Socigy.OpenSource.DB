using System.Linq;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>
/// An [Encrypted] column stores bytea ciphertext unconditionally. Its type must win regardless of attribute order
/// (a co-located [StringLength]/[Column(Type)] runs later in the attribute loop and would overwrite "bytea"), and
/// it must carry no plaintext SQL default (a property initializer would emit an invalid bytea DEFAULT).
/// </summary>
[TestFixture]
public class AssemblyAnalyzerEncryptedTests
{
    private static string EncryptedColumnType(string attributes)
    {
        string model = $@"
using System;
using Socigy.OpenSource.DB.Attributes;
namespace Fixture
{{
    [Table(""t"")] public partial class T
    {{
        [PrimaryKey] public Guid Id {{ get; set; }}
        {attributes} public string Secret {{ get; set; }} = "" "";
    }}
}}";
        var schema = AnalyzerModelCompiler.Analyze(model);
        var col = schema.Tables.First(t => t.Name == "t").Columns.First(c => c.Name == "secret");
        return col.DatabaseType;
    }

    // Both orderings must yield bytea — the encrypted type is authoritative, not attribute-order-dependent.
    [TestCase("[Encrypted, StringLength(10)]")]
    [TestCase("[StringLength(10), Encrypted]")]
    [TestCase("[Encrypted, Column(Type = \"text\")]")]
    [TestCase("[Column(Type = \"text\"), Encrypted]")]
    public void EncryptedColumn_IsAlwaysBytea_RegardlessOfAttributeOrder(string attributes)
    {
        Assert.That(EncryptedColumnType(attributes), Is.EqualTo("bytea"),
            "an [Encrypted] column must be bytea no matter what other type-setting attributes sit next to it");
    }

    [Test]
    public void EncryptedColumn_WithInitializer_HasNoPlaintextDefault()
    {
        const string model = @"
using System;
using Socigy.OpenSource.DB.Attributes;
namespace Fixture
{
    [Table(""t"")] public partial class T
    {
        [PrimaryKey] public Guid Id { get; set; }
        [Encrypted] public string Secret { get; set; } = ""hello"";
    }
}";
        var schema = AnalyzerModelCompiler.Analyze(model);
        var col = schema.Tables.First(t => t.Name == "t").Columns.First(c => c.Name == "secret");

        Assert.That(col.DatabaseType, Is.EqualTo("bytea"));
        Assert.That(col.DefaultValue, Is.Null,
            "an encrypted column must not carry a plaintext SQL default (DEFAULT 'hello' on a bytea column fails at apply)");
    }
}
