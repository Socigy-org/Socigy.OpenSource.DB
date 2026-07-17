using System;
using System.Threading;
using System.Threading.Tasks;
using Socigy.OpenSource.DB.HashiCorp.Tests.Fakes;

namespace Socigy.OpenSource.DB.HashiCorp.Tests;

/// <summary>
/// Background rotation must survive an interval longer than System.Threading.Timer accepts as a dueTime
/// (~49.7 days). The default RotationInterval is 90 days, so simply turning the feature on used to throw
/// ArgumentOutOfRangeException out of StartAsync and kill the host at boot.
/// </summary>
[TestFixture]
public class VaultRotationSchedulingTests
{
    // Documents the hazard the clamp exists for: this is exactly what StartAsync used to do with the
    // default 90-day RotationInterval, and it is why enabling the feature killed the host at boot.
    [Test]
    public void Arming_a_timer_with_the_raw_90_day_default_throws()
    {
        using var t = new Timer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => t.Change(TimeSpan.FromDays(90), Timeout.InfiniteTimeSpan));
        Assert.That(ex!.ParamName, Is.EqualTo("dueTime"));
    }

    [Test]
    public async Task Default_90_day_interval_starts_without_throwing()
    {
        var fake = new FakeVaultEncryptor();
        using var svc = new VaultEncryptionRotationService(
            fake, TimeSpan.FromDays(90));

        Assert.DoesNotThrowAsync(() => svc.StartAsync(default),
            "the documented EnableBackgroundRotation=true default must not crash host startup");
        await svc.StopAsync(default);

        Assert.That(fake.RotateCount, Is.EqualTo(0),
            "clamping the arm must not rotate immediately — it only shortens the wait");
    }

    [Test]
    public async Task Short_interval_actually_rotates()
    {
        var fake = new FakeVaultEncryptor();
        using var svc = new VaultEncryptionRotationService(
            fake, TimeSpan.FromMilliseconds(30));

        await svc.StartAsync(default);
        // Bounded poll: no real Vault, no fixed sleep.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (fake.RotateCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await svc.StopAsync(default);

        Assert.That(fake.RotateCount, Is.GreaterThanOrEqualTo(1), "a short interval must still fire");
    }

    [Test]
    public async Task Stop_prevents_further_rotation()
    {
        var fake = new FakeVaultEncryptor();
        using var svc = new VaultEncryptionRotationService(
            fake, TimeSpan.FromMilliseconds(20));
        await svc.StartAsync(default);
        await svc.StopAsync(default);

        int after = fake.RotateCount;
        await Task.Delay(80);
        Assert.That(fake.RotateCount, Is.EqualTo(after), "a stopped service must not re-arm");
    }
}
