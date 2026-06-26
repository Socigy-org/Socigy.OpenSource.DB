using System;
using Socigy.OpenSource.DB.HashiCorp;

namespace Socigy.OpenSource.DB.HashiCorp.Tests;

/// <summary>
/// The envelope keyring is persisted as a single delimiter-separated field. The format must round-trip and must
/// not be confused by the base64 padding ('=') and colons inside a <c>vault:vN:…</c> wrapped key.
/// </summary>
[TestFixture]
public class VaultKeyringTests
{
    [Test]
    public void Round_trips_current_and_wrapped_keys()
    {
        var keyring = new VaultKeyring { Current = 2 };
        keyring.Keys[1] = "vault:v1:abcDEF123==";       // base64 padding present
        keyring.Keys[2] = "vault:v1:zzz+/equalsPad==";

        var parsed = VaultKeyring.Parse(keyring.Serialize());

        Assert.That(parsed.Current, Is.EqualTo(2));
        Assert.That(parsed.Keys.Count, Is.EqualTo(2));
        Assert.That(parsed.Keys[1], Is.EqualTo("vault:v1:abcDEF123=="));
        Assert.That(parsed.Keys[2], Is.EqualTo("vault:v1:zzz+/equalsPad=="));
    }

    [Test]
    public void Next_id_is_max_plus_one()
    {
        var keyring = new VaultKeyring { Current = 3 };
        keyring.Keys[1] = "vault:v1:a==";
        keyring.Keys[3] = "vault:v1:b==";
        Assert.That(keyring.NextId(), Is.EqualTo(4));
    }

    [Test]
    public void Parsing_empty_or_keyless_throws()
    {
        Assert.Throws<FormatException>(() => VaultKeyring.Parse(""));
        Assert.Throws<FormatException>(() => VaultKeyring.Parse("current=1"));
    }
}
