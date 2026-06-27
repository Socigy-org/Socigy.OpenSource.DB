using NUnit.Framework;
using Socigy.OpenSource.DB.SourceGenerator;

namespace Socigy.OpenSource.DB.SourceGenerator.Tests;

/// <summary>
/// The procedure-DTO mapper method name (Map_{id} / Ordinals_{id}) is derived from the DTO's fully-qualified name.
/// Sanitizing every non-alphanumeric char to '_' alone collapsed two DISTINCT types whose FQNs differ only at a
/// separator (e.g. `A.B.C` vs namespace `A_B` type `C`) to the same id, producing duplicate generated methods
/// (CS0111). A stable FQN hash is appended so the ids stay distinct.
/// </summary>
[TestFixture]
public class DtoMapperIdTests
{
    private static string Id(string fqn) => DtoMapperGenerator.Sanitize(fqn) + "_" + DtoMapperGenerator.StableHash(fqn);

    [Test]
    public void SeparatorOnlyDifference_ProducesDistinctIds()
    {
        // Both sanitize to "A_B_C", so without the hash they would collide.
        Assert.That(DtoMapperGenerator.Sanitize("A.B.C"), Is.EqualTo(DtoMapperGenerator.Sanitize("A_B.C")),
            "precondition: the two FQNs sanitize to the same identifier");
        Assert.That(Id("A.B.C"), Is.Not.EqualTo(Id("A_B.C")),
            "the full mapper id must differ so the generated methods do not collide (CS0111)");
    }

    [Test]
    public void StableHash_IsDeterministic()
    {
        Assert.That(DtoMapperGenerator.StableHash("Acme.Reports.UserRow"),
            Is.EqualTo(DtoMapperGenerator.StableHash("Acme.Reports.UserRow")),
            "the hash must be reproducible across runs so generated names are stable");
    }
}
