using System;
using Socigy.OpenSource.DB.HashiCorp.Internal;

namespace Socigy.OpenSource.DB.HashiCorp.Tests;

/// <summary>
/// Renewal must be scheduled from the actual remaining lifetime (lease/token TTL), not a fixed interval that
/// could outlast it and let credentials expire.
/// </summary>
[TestFixture]
public class VaultRenewalTests
{
    [Test]
    public void Renews_at_two_thirds_of_remaining_lifetime()
    {
        Assert.That(VaultRenewal.NextDelay(3600, TimeSpan.FromMinutes(30)),
            Is.EqualTo(TimeSpan.FromSeconds(2400)));
    }

    // A lease long enough that the 30s floor still fits before it expires (2/3*40 = 26.7 < 30 < 40) floors to 30.
    [Test]
    public void Short_lifetime_is_floored_when_floor_still_fits()
    {
        Assert.That(VaultRenewal.NextDelay(40, TimeSpan.FromMinutes(30)), Is.EqualTo(VaultRenewal.Floor));
    }

    // Regression: a very short lease must NOT be floored past its own expiry. For a 30s lease, the 30s floor
    // would renew at/after expiry, so it renews at 2/3 (20s) instead — before the credential dies.
    [Test]
    public void Very_short_lifetime_renews_before_expiry_not_floored_past_it()
    {
        var delay = VaultRenewal.NextDelay(30, TimeSpan.FromMinutes(30));
        Assert.That(delay, Is.EqualTo(TimeSpan.FromSeconds(20)));
        Assert.That(delay, Is.LessThan(TimeSpan.FromSeconds(30)), "must renew before the 30s lease expires");
    }

    [Test]
    public void Unknown_lifetime_uses_fallback()
    {
        Assert.That(VaultRenewal.NextDelay(null, TimeSpan.FromMinutes(30)), Is.EqualTo(TimeSpan.FromMinutes(30)));
    }

    [Test]
    public void Nonpositive_lifetime_uses_fallback()
    {
        Assert.That(VaultRenewal.NextDelay(0, TimeSpan.FromMinutes(10)), Is.EqualTo(TimeSpan.FromMinutes(10)));
    }

    [Test]
    public void Tiny_fallback_is_floored()
    {
        Assert.That(VaultRenewal.NextDelay(null, TimeSpan.FromSeconds(5)), Is.EqualTo(VaultRenewal.Floor));
    }

    // ── Timer max-delay clamping ────────────────────────────────────────────────────
    // System.Threading.Timer rejects a dueTime above uint.MaxValue-1 ms (~49.7 days). Both renewal services
    // feed NextDelay straight into Timer.Change and catch only ObjectDisposedException, so an unclamped delay
    // throws ArgumentOutOfRangeException on a timer thread and takes the process down.

    [Test]
    public void Very_long_lifetime_is_clamped_to_the_timer_limit()
    {
        // 2/3 * 80 days = 53.3 days, past the ~49.7-day Timer cap.
        var delay = VaultRenewal.NextDelay(TimeSpan.FromDays(80).TotalSeconds, TimeSpan.FromMinutes(30));
        Assert.That(delay, Is.EqualTo(VaultRenewal.MaxTimerDelay));
        Assert.That(delay, Is.LessThanOrEqualTo(VaultRenewal.MaxTimerDelay));
    }

    [Test]
    public void Very_long_fallback_is_clamped_to_the_timer_limit()
    {
        // The unknown-TTL path returns the configured RefreshInterval verbatim.
        Assert.That(VaultRenewal.NextDelay(null, TimeSpan.FromDays(60)), Is.EqualTo(VaultRenewal.MaxTimerDelay));
    }

    [Test]
    public void ClampToTimer_bounds_both_ends_and_leaves_normal_delays_alone()
    {
        Assert.That(VaultRenewal.ClampToTimer(TimeSpan.FromDays(90)), Is.EqualTo(VaultRenewal.MaxTimerDelay));
        Assert.That(VaultRenewal.ClampToTimer(TimeSpan.FromSeconds(-5)), Is.EqualTo(TimeSpan.Zero));
        Assert.That(VaultRenewal.ClampToTimer(TimeSpan.FromMinutes(5)), Is.EqualTo(TimeSpan.FromMinutes(5)));
    }

    [Test]
    public void MaxTimerDelay_is_within_what_Timer_accepts()
    {
        Assert.That(VaultRenewal.MaxTimerDelay.TotalMilliseconds, Is.EqualTo(uint.MaxValue - 1));
        using var t = new System.Threading.Timer(_ => { }, null, System.Threading.Timeout.InfiniteTimeSpan,
            System.Threading.Timeout.InfiniteTimeSpan);
        Assert.DoesNotThrow(() => t.Change(VaultRenewal.MaxTimerDelay, System.Threading.Timeout.InfiniteTimeSpan),
            "the constant must be armable, not one tick over the limit");
    }

    // ── Long-interval re-arm math (used by the rotation service) ────────────────────
    [Test]
    public void NextRotationArm_hops_in_clamped_steps_until_the_interval_elapses()
    {
        var arm = VaultRenewal.NextRotationArm(TimeSpan.FromDays(90), out bool rotateNow);
        Assert.That(arm, Is.EqualTo(VaultRenewal.MaxTimerDelay));
        Assert.That(rotateNow, Is.False, "90 days has not elapsed; hop, don't rotate");

        arm = VaultRenewal.NextRotationArm(TimeSpan.FromDays(40), out rotateNow);
        Assert.That(arm, Is.EqualTo(TimeSpan.FromDays(40)), "a remainder under the cap arms verbatim");
        Assert.That(rotateNow, Is.False);

        VaultRenewal.NextRotationArm(TimeSpan.Zero, out rotateNow);
        Assert.That(rotateNow, Is.True);

        VaultRenewal.NextRotationArm(TimeSpan.FromMilliseconds(10), out rotateNow);
        Assert.That(rotateNow, Is.True, "within tolerance counts as elapsed (a timer may fire a hair early)");

        VaultRenewal.NextRotationArm(TimeSpan.FromSeconds(-1), out rotateNow);
        Assert.That(rotateNow, Is.True, "overshoot counts as elapsed");
    }
}
