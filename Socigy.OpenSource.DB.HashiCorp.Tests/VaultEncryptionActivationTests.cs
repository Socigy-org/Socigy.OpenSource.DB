using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Socigy.OpenSource.DB.Core.Encryption;
using Socigy.OpenSource.DB.HashiCorp.Tests.Fakes;

namespace Socigy.OpenSource.DB.HashiCorp.Tests;

/// <summary>
/// Encryption used to be activated only by the priming hosted service, which does not run until app.Run() —
/// so the documented `await app.EnsureLatestMyDbMigration()` between Build() and Run() threw on any
/// [Encrypted] column. UseSocigyVaultEncryption() activates every registered profile up front, and the
/// hosted service must then find the work already done rather than hit Vault twice.
/// </summary>
[TestFixture]
public class VaultEncryptionActivationTests
{
    // SocigyFieldEncryption is process-wide static and profiles are never removed, so every test uses its own
    // profile name and never asserts on the shared default.
    private static string NewProfile() => "act-" + Guid.NewGuid().ToString("N");

    private static ServiceProvider BuildWith(params IVaultEncryptionPrimer[] primers)
    {
        var services = new ServiceCollection();
        foreach (var p in primers)
            services.AddSingleton(p);
        services.AddHostedService(sp => new VaultEncryptionPrimingService(sp.GetServices<IVaultEncryptionPrimer>()));
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Activates_every_registered_profile_before_the_host_starts()
    {
        var defaultEnc = new FakeVaultEncryptor();
        var profiled = new FakeVaultEncryptor();
        string profile = NewProfile();
        using var sp = BuildWith(new VaultEncryptionPrimer(defaultEnc, null),
                                 new VaultEncryptionPrimer(profiled, profile));

        await sp.UseSocigyVaultEncryption();

        Assert.That(defaultEnc.RefreshCount, Is.EqualTo(1));
        Assert.That(profiled.RefreshCount, Is.EqualTo(1), "every profile is primed, not just the first/default");
        Assert.That(SocigyFieldEncryption.IsProfileConfigured(profile), Is.True,
            "the profile must be usable before EnsureLatestMyDbMigration() runs");
    }

    [Test]
    public async Task Priming_is_idempotent_so_host_start_does_not_re_hit_vault()
    {
        var enc = new FakeVaultEncryptor();
        using var sp = BuildWith(new VaultEncryptionPrimer(enc, NewProfile()));

        await sp.UseSocigyVaultEncryption();
        Assert.That(enc.RefreshCount, Is.EqualTo(1));

        // What the host does at Run().
        var primingService = sp.GetServices<IHostedService>().OfType<VaultEncryptionPrimingService>().Single();
        await primingService.StartAsync(default);
        await sp.UseSocigyVaultEncryption();

        Assert.That(enc.RefreshCount, Is.EqualTo(1), "already-primed encryptors must not be refreshed again");
    }

    [Test]
    public async Task A_failed_activation_is_retried_rather_than_cached()
    {
        // A transient Vault outage during UseSocigyVaultEncryption() must not permanently poison startup.
        var enc = new FakeVaultEncryptor { FailWith = new InvalidOperationException("vault sealed") };
        string profile = NewProfile();
        using var sp = BuildWith(new VaultEncryptionPrimer(enc, profile));

        Assert.ThrowsAsync<InvalidOperationException>(() => sp.UseSocigyVaultEncryption());
        Assert.That(enc.RefreshCount, Is.EqualTo(1));
        Assert.That(SocigyFieldEncryption.IsProfileConfigured(profile), Is.False, "nothing was activated");

        enc.FailWith = null;
        await sp.UseSocigyVaultEncryption();

        Assert.That(enc.RefreshCount, Is.EqualTo(2), "the faulted attempt must not be memoized");
        Assert.That(SocigyFieldEncryption.IsProfileConfigured(profile), Is.True);
    }

    [Test]
    public void No_registered_encryptors_is_a_no_op_not_a_throw()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        Assert.DoesNotThrowAsync(() => sp.UseSocigyVaultEncryption());
    }

    [Test]
    public void Null_arguments_are_rejected()
    {
        Assert.ThrowsAsync<ArgumentNullException>(() => ((IHost)null!).UseSocigyVaultEncryption());
        Assert.ThrowsAsync<ArgumentNullException>(() => ((IServiceProvider)null!).UseSocigyVaultEncryption());
    }
}
