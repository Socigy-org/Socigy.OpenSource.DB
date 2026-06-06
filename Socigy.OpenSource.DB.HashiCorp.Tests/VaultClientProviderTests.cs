using Socigy.OpenSource.DB.HashiCorp;

namespace Socigy.OpenSource.DB.HashiCorp.Tests;

/// <summary>
/// The provider can only obtain a brand-new token (re-login) with AppRole credentials; a static token can be
/// renewed but never replaced. The decision must reflect that.
/// </summary>
[TestFixture]
public class VaultClientProviderTests
{
    [Test]
    public void Can_relogin_with_approle_credentials()
    {
        var provider = new VaultClientProvider(new VaultCredentialsOptions
        {
            Address = "http://127.0.0.1:8200",
            AppRoleId = "role",
            AppRoleSecretId = "secret",
        });

        Assert.That(provider.CanRelogin, Is.True);
    }

    [Test]
    public void Cannot_relogin_with_a_static_token()
    {
        var provider = new VaultClientProvider(new VaultCredentialsOptions
        {
            Address = "http://127.0.0.1:8200",
            Token = "s.sometoken",
        });

        Assert.That(provider.CanRelogin, Is.False);
    }
}
