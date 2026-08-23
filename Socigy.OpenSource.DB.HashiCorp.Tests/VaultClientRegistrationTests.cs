using System;
using Microsoft.Extensions.DependencyInjection;

namespace Socigy.OpenSource.DB.HashiCorp.Tests;

/// <summary>
/// Every Vault feature shares one <see cref="VaultClientProvider"/>, so they must agree on how to reach Vault.
///
/// Each helper used to do its own <c>TryAddSingleton</c> with its options captured in the factory lambda:
/// first registration won and every later helper's <c>Address</c>, <c>Token</c>, <c>AppRoleId</c> and
/// <c>AppRoleSecretId</c> were captured in a lambda that was never invoked, then silently discarded. Those are
/// the auth identity for the whole process, not incidental settings — pointing field encryption and database
/// credentials at different Vault servers quietly used one of them for both.
///
/// These build a real container; nothing contacts Vault (VaultClientProvider's constructor is offline).
/// </summary>
[TestFixture]
public class VaultClientRegistrationTests
{
    private const string Address = "http://127.0.0.1:8200";

    [Test]
    public void Identical_settings_across_features_share_one_client()
    {
        var services = new ServiceCollection();
        services.AddSocigyVaultEnvelopeEncryption(o =>
        {
            o.Address = Address;
            o.Token = "test-token";
            o.TransitKeyName = "socigy-db";
        });
        services.AddSocigyVaultCredentials(o =>
        {
            o.Address = Address;
            o.Token = "test-token";
            o.DatabaseRoles["AuthDb"] = "auth-role";
        });

        using var sp = services.BuildServiceProvider();

        Assert.That(sp.GetRequiredService<VaultClientProvider>(), Is.Not.Null,
            "sharing one client is still the intended default — it keeps a single auth token renewed");
    }

    // The foot-gun this exists for: now an error the developer sees, rather than a silent choice.
    [TestCase("Address")]
    [TestCase("Token")]
    [TestCase("AppRoleId")]
    [TestCase("AppRoleSecretId")]
    public void Conflicting_connection_settings_throw_naming_both_helpers(string field)
    {
        var services = new ServiceCollection();
        services.AddSocigyVaultEnvelopeEncryption(o =>
        {
            o.Address = Address;
            o.Token = "first-token";
            o.AppRoleId = "role-a";
            o.AppRoleSecretId = "secret-a";
        });

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddSocigyVaultCredentials(o =>
        {
            o.Address = field == "Address" ? "http://other:8200" : Address;
            o.Token = field == "Token" ? "second-token" : "first-token";
            o.AppRoleId = field == "AppRoleId" ? "role-b" : "role-a";
            o.AppRoleSecretId = field == "AppRoleSecretId" ? "secret-b" : "secret-a";
            o.DatabaseRoles["AuthDb"] = "auth-role";
        }));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("AddSocigyVaultCredentials"), "name the helper that failed");
            Assert.That(ex.Message, Does.Contain("AddSocigyVaultEnvelopeEncryption"), "and the one it conflicts with");
            Assert.That(ex.Message, Does.Contain(field), "and which setting disagrees");
        });
    }

    // A secret in an exception message ends up in logs and crash reports.
    [Test]
    public void The_conflict_message_never_quotes_the_secret_values()
    {
        var services = new ServiceCollection();
        services.AddSocigyVaultEnvelopeEncryption(o =>
        {
            o.Address = Address;
            o.Token = "super-secret-first";
        });

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddSocigyVaultCredentials(o =>
        {
            o.Address = Address;
            o.Token = "super-secret-second";
            o.DatabaseRoles["AuthDb"] = "auth-role";
        }));

        Assert.That(ex!.Message, Does.Not.Contain("super-secret"));
    }

    // The normal shape once AddSocigyVaultClient carries the connection: features leave it unset.
    [Test]
    public void A_feature_that_omits_connection_settings_does_not_conflict()
    {
        var services = new ServiceCollection();
        services.AddSocigyVaultClient(o =>
        {
            o.Address = "https://vault.example.com:8200";
            o.AppRoleId = "role-a";
            o.AppRoleSecretId = "secret-a";
        });

        Assert.DoesNotThrow(() => services.AddSocigyVaultCredentials(o =>
        {
            o.Address = null!;   // left at whatever the feature options default to
            o.DatabaseRoles["AuthDb"] = "auth-role";
        }));
    }

    [Test]
    public void AddSocigyVaultClient_registers_the_shared_provider_on_its_own()
    {
        var services = new ServiceCollection();
        services.AddSocigyVaultClient(o =>
        {
            o.Address = Address;
            o.Token = "test-token";
        });

        using var sp = services.BuildServiceProvider();
        Assert.That(sp.GetService<VaultClientProvider>(), Is.Not.Null);
    }
}
