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
}
