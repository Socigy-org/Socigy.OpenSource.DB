using System.Data.Common;
using Socigy.OpenSource.DB.HashiCorp.Internal;

namespace Socigy.OpenSource.DB.HashiCorp.Tests;

/// <summary>
/// Vault-issued passwords routinely contain ';', '=', spaces and quotes. The composed connection string must
/// round-trip them through a parser, not corrupt them via naive concatenation.
/// </summary>
[TestFixture]
public class VaultConnectionStringTests
{
    private static (string user, string pass) Parse(string connectionString)
    {
        var b = new DbConnectionStringBuilder { ConnectionString = connectionString };
        return ((string)b["Username"], (string)b["Password"]);
    }

    [Test]
    public void Password_with_special_characters_round_trips()
    {
        string cs = VaultConnectionString.Compose(
            "Host=db;Port=5432;Pooling=true", "v-token-abc", "p;a=ss\"wo rd';x");

        var (user, pass) = Parse(cs);
        Assert.That(user, Is.EqualTo("v-token-abc"));
        Assert.That(pass, Is.EqualTo("p;a=ss\"wo rd';x"));
    }

    [Test]
    public void Base_settings_are_preserved()
    {
        string cs = VaultConnectionString.Compose("Host=db;Port=5432;Pooling=true", "u", "p");
        var b = new DbConnectionStringBuilder { ConnectionString = cs };

        Assert.That(b["Host"], Is.EqualTo("db"));
        Assert.That(b["Port"].ToString(), Is.EqualTo("5432"));
        Assert.That(b["Pooling"].ToString(), Is.EqualTo("true"));
    }

    [Test]
    public void Trailing_semicolon_in_base_does_not_break_composition()
    {
        string cs = VaultConnectionString.Compose("Host=db;Port=5432;", "u", "p");
        var (user, pass) = Parse(cs);
        Assert.That(user, Is.EqualTo("u"));
        Assert.That(pass, Is.EqualTo("p"));
    }
}
