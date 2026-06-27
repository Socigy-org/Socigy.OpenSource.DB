using System;

namespace Socigy.OpenSource.DB.HashiCorp.Internal
{
#nullable enable
    internal static class VaultRenewal
    {
        /// <summary>Don't reschedule tighter than this, so a tiny TTL can't turn into a busy loop.</summary>
        public static readonly TimeSpan Floor = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How long to wait before renewing something that expires in <paramref name="secondsRemaining"/>.
        /// Renews at ~2/3 of the remaining lifetime so there is slack for retries before expiry. When the
        /// remaining lifetime is unknown (null/non-positive) the configured <paramref name="fallback"/> is
        /// used. The result is floored at <see cref="Floor"/>.
        /// </summary>
        public static TimeSpan NextDelay(double? secondsRemaining, TimeSpan fallback)
        {
            if (secondsRemaining is double s && s > 0)
            {
                var ttl = TimeSpan.FromSeconds(s);
                var candidate = TimeSpan.FromSeconds(s * 2.0 / 3.0);
                if (candidate >= Floor)
                    return candidate;
                // 2/3 of the TTL is below the busy-loop floor. Prefer the floor, but NEVER schedule the renewal
                // at or after the lease expires — for a very short lease (floor >= TTL) renew at 2/3 instead, so
                // the credential is always renewed before it dies even if that means a tighter loop.
                return Floor < ttl ? Floor : candidate;
            }

            // Unknown / non-positive TTL: fall back to the configured interval, floored against a busy loop.
            return fallback < Floor ? Floor : fallback;
        }
    }
#nullable disable
}
