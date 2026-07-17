using System;

namespace Socigy.OpenSource.DB.HashiCorp.Internal
{
#nullable enable
    internal static class VaultRenewal
    {
        /// <summary>Don't reschedule tighter than this, so a tiny TTL can't turn into a busy loop.</summary>
        public static readonly TimeSpan Floor = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The largest dueTime <see cref="System.Threading.Timer"/> accepts (0xFFFFFFFE ms, ~49.7 days).
        /// Arming beyond this throws <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>
        public static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

        /// <summary>A timer may fire a hair early; treat a remainder under this as "elapsed".</summary>
        public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// Clamps a delay into the range <see cref="System.Threading.Timer"/> accepts. Waking early is harmless
        /// (a renewal re-reads the TTL; a rotation re-checks the elapsed time), whereas arming past
        /// <see cref="MaxTimerDelay"/> throws on the caller's thread.
        /// </summary>
        public static TimeSpan ClampToTimer(TimeSpan delay)
            => delay < TimeSpan.Zero ? TimeSpan.Zero
             : delay > MaxTimerDelay ? MaxTimerDelay
             : delay;

        /// <summary>
        /// Decides the next arm for an interval that may exceed <see cref="MaxTimerDelay"/>. Returns the
        /// (clamped) delay to arm; <paramref name="rotateNow"/> is set when the full interval has actually
        /// elapsed, so a caller can hop in <see cref="MaxTimerDelay"/> steps until it has.
        /// </summary>
        public static TimeSpan NextRotationArm(TimeSpan remaining, out bool rotateNow)
        {
            rotateNow = remaining <= Tolerance;
            return rotateNow ? TimeSpan.Zero : ClampToTimer(remaining);
        }

        /// <summary>
        /// How long to wait before renewing something that expires in <paramref name="secondsRemaining"/>.
        /// Renews at ~2/3 of the remaining lifetime so there is slack for retries before expiry. When the
        /// remaining lifetime is unknown (null/non-positive) the configured <paramref name="fallback"/> is
        /// used. The result is floored at <see cref="Floor"/> and clamped to <see cref="MaxTimerDelay"/>.
        /// </summary>
        public static TimeSpan NextDelay(double? secondsRemaining, TimeSpan fallback)
        {
            if (secondsRemaining is double s && s > 0)
            {
                var ttl = TimeSpan.FromSeconds(s);
                var candidate = TimeSpan.FromSeconds(s * 2.0 / 3.0);
                if (candidate >= Floor)
                    return ClampToTimer(candidate);
                // 2/3 of the TTL is below the busy-loop floor. Prefer the floor, but NEVER schedule the renewal
                // at or after the lease expires — for a very short lease (floor >= TTL) renew at 2/3 instead, so
                // the credential is always renewed before it dies even if that means a tighter loop.
                return Floor < ttl ? Floor : candidate;
            }

            // Unknown / non-positive TTL: fall back to the configured interval, floored against a busy loop.
            return ClampToTimer(fallback < Floor ? Floor : fallback);
        }
    }
#nullable disable
}
