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
            TimeSpan candidate = secondsRemaining.HasValue && secondsRemaining.Value > 0
                ? TimeSpan.FromSeconds(secondsRemaining.Value * 2.0 / 3.0)
                : fallback;

            return candidate < Floor ? Floor : candidate;
        }
    }
#nullable disable
}
