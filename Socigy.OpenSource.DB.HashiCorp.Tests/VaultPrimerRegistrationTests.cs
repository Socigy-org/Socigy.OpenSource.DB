using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Socigy.OpenSource.DB.HashiCorp.Tests;

/// <summary>
/// Registering a default encryptor plus a profiled one must activate BOTH. The primers used to be registered
/// via AddHostedService, whose TryAddEnumerable de-duplicates by implementation type — so the second
/// VaultEncryptionPrimingService was silently dropped and the [Encrypted(Profile = "…")] columns it was
/// supposed to activate threw at the first write. These build a real container; nothing contacts Vault
/// (VaultClientProvider's ctor is offline and no primer is run here).
/// </summary>
[TestFixture]
public class VaultPrimerRegistrationTests
{
    private static ServiceProvider BuildEnvelopePlusTransit()
    {
        var services = new ServiceCollection();
        services.AddSocigyVaultEnvelopeEncryption(o =>
        {
            o.Address = "http://127.0.0.1:8200";
            o.Token = "test-token";
            o.TransitKeyName = "socigy-db";
        });
        services.AddSocigyVaultTransitEncryption(o =>
        {
            o.Address = "http://127.0.0.1:8200";
            o.Token = "test-token";
            o.TransitKeyName = "socigy-eaas";
            o.Profile = "transit";
        });
        return services.BuildServiceProvider();
    }

    // Pins the framework behavior this whole split exists for: AddHostedService goes through
    // TryAddEnumerable, which de-duplicates by IMPLEMENTATION TYPE — two registrations of the same service
    // type collapse to one, no matter that the factories close over different state (here, different
    // profiles). That is why per-profile primers cannot be hosted services, and why the single collector
    // service can safely be registered once per helper call.
    [Test]
    public void AddHostedService_deduplicates_by_implementation_type()
    {
        var services = new ServiceCollection();
        services.AddHostedService(_ => new ProbeHostedService("first"));
        services.AddHostedService(_ => new ProbeHostedService("second"));
        using var sp = services.BuildServiceProvider();

        var probes = sp.GetServices<IHostedService>().OfType<ProbeHostedService>().ToArray();
        Assert.That(probes, Has.Length.EqualTo(1), "the second registration is silently dropped");
        Assert.That(probes[0].Tag, Is.EqualTo("first"), "first one wins");

        // Whereas a plain enumerable singleton keeps both — the shape the primers now use.
        var kept = new ServiceCollection();
        kept.AddSingleton<IHostedService>(_ => new ProbeHostedService("first"));
        kept.AddSingleton<IHostedService>(_ => new ProbeHostedService("second"));
        using var sp2 = kept.BuildServiceProvider();
        Assert.That(sp2.GetServices<IHostedService>().OfType<ProbeHostedService>().Count(), Is.EqualTo(2));
    }

    private sealed class ProbeHostedService : IHostedService
    {
        public ProbeHostedService(string tag) => Tag = tag;
        public string Tag { get; }
        public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
    }

    [Test]
    public void Envelope_plus_profiled_transit_registers_a_primer_for_each()
    {
        using var sp = BuildEnvelopePlusTransit();

        var profiles = sp.GetServices<IVaultEncryptionPrimer>().Select(p => p.Profile).ToArray();

        Assert.That(profiles, Has.Length.EqualTo(2),
            "both the default and the profiled encryptor must get a primer (the profiled one used to be dropped)");
        Assert.That(profiles, Is.EquivalentTo(new string?[] { null, "transit" }));
    }

    [Test]
    public void One_priming_service_collects_every_primer()
    {
        using var sp = BuildEnvelopePlusTransit();

        // The collector is registered once per helper call but must collapse to a single instance —
        // this is the de-duplication that is now correct rather than harmful.
        var hosted = sp.GetServices<IHostedService>().ToArray();
        Assert.That(hosted.OfType<VaultEncryptionPrimingService>().Count(), Is.EqualTo(1));

        // Guards the pre-existing intentional collapse of the shared auth renewer.
        Assert.That(hosted.OfType<VaultAuthRenewalService>().Count(), Is.EqualTo(1));
    }

    [Test]
    public void Transit_only_registration_still_primes_its_profile()
    {
        var services = new ServiceCollection();
        services.AddSocigyVaultTransitEncryption(o =>
        {
            o.Address = "http://127.0.0.1:8200";
            o.Token = "test-token";
            o.Profile = "solo";
        });
        using var sp = services.BuildServiceProvider();

        Assert.That(sp.GetServices<IVaultEncryptionPrimer>().Single().Profile, Is.EqualTo("solo"));
    }

    [Test]
    public void Background_rotation_is_registered_for_transit_too()
    {
        // EnableBackgroundRotation was read only by the envelope helper, so turning it on in EaaS-direct
        // mode silently did nothing.
        var services = new ServiceCollection();
        services.AddSocigyVaultTransitEncryption(o =>
        {
            o.Address = "http://127.0.0.1:8200";
            o.Token = "test-token";
            o.Profile = "rot";
            o.EnableBackgroundRotation = true;
            o.RotationInterval = TimeSpan.FromDays(30);
        });
        using var sp = services.BuildServiceProvider();

        Assert.That(sp.GetServices<IHostedService>().OfType<VaultEncryptionRotationService>().Count(),
            Is.EqualTo(1), "EnableBackgroundRotation must register a rotator in transit mode as well");
    }

    [Test]
    public void Both_modes_rotating_keeps_both_rotators()
    {
        // AddHostedService would de-duplicate the second VaultEncryptionRotationService by type.
        var services = new ServiceCollection();
        services.AddSocigyVaultEnvelopeEncryption(o =>
        {
            o.Address = "http://127.0.0.1:8200"; o.Token = "t";
            o.EnableBackgroundRotation = true; o.RotationInterval = TimeSpan.FromDays(30);
        });
        services.AddSocigyVaultTransitEncryption(o =>
        {
            o.Address = "http://127.0.0.1:8200"; o.Token = "t"; o.Profile = "transit";
            o.EnableBackgroundRotation = true; o.RotationInterval = TimeSpan.FromDays(30);
        });
        using var sp = services.BuildServiceProvider();

        Assert.That(sp.GetServices<IHostedService>().OfType<VaultEncryptionRotationService>().Count(),
            Is.EqualTo(2), "each mode rotates its own key; neither rotator may be dropped");
    }
}
