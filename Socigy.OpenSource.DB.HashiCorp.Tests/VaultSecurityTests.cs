using Socigy.OpenSource.DB.HashiCorp.Internal;

namespace Socigy.OpenSource.DB.HashiCorp.Tests;

[TestFixture]
public class VaultSecurityTests
{
    [TestCase("https://vault.example.com:8200", false)] // encrypted — fine
    [TestCase("http://vault.example.com:8200", true)]   // plaintext to a remote host — insecure
    [TestCase("http://127.0.0.1:8200", false)]          // loopback — fine for dev
    [TestCase("http://localhost:8200", false)]          // loopback — fine for dev
    [TestCase("http://[::1]:8200", false)]              // loopback — fine for dev
    [TestCase(null, false)]
    [TestCase("", false)]
    public void Flags_only_plaintext_http_to_remote_hosts(string? address, bool expected)
    {
        Assert.That(VaultSecurity.IsInsecureRemote(address), Is.EqualTo(expected));
    }
}
