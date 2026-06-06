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

    [Test]
    public void Short_lifetime_is_floored()
    {
        Assert.That(VaultRenewal.NextDelay(10, TimeSpan.FromMinutes(30)), Is.EqualTo(VaultRenewal.Floor));
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
