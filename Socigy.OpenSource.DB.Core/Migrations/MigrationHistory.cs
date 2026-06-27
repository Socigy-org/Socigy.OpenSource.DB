using System;
using System.Collections.Generic;
using System.Linq;

namespace Socigy.OpenSource.DB.Core.Migrations
{
#nullable enable
    /// <summary>
    /// Pure helpers for reasoning about migration order and applied state. Kept free of any database or
    /// generated-code dependency so the logic is unit-testable in isolation.
    /// </summary>
    public static class MigrationHistory
    {
        /// <summary>
        /// Orders migrations oldest-&gt;newest by following the <c>PreviousId</c> chain rather than by sorting
        /// their ids. Id-sorting is fragile: ids are minute-granularity timestamps, so two migrations created
        /// in the same minute sort arbitrarily, and a user-named or non-timestamped id may not sort at all.
        /// The chain is the source of truth.
        /// </summary>
        /// <param name="migrations">(Id, PreviousId) for every local migration. PreviousId is null for the first.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the chain is not a single, complete line: duplicate ids, more than one root, a missing
        /// predecessor, a fork, or a cycle. A broken chain must fail loudly rather than apply in a guessed order.
        /// </exception>
        public static IReadOnlyList<string> OrderByChain(IEnumerable<(string Id, string? PreviousId)> migrations)
        {
            if (migrations == null) throw new ArgumentNullException(nameof(migrations));

            var prevOf = new Dictionary<string, string?>();
            var nextOf = new Dictionary<string, string>();
            foreach (var (id, previousId) in migrations)
            {
                if (id == null) throw new InvalidOperationException("A migration has a null Id.");
                if (prevOf.ContainsKey(id))
                    throw new InvalidOperationException($"Duplicate migration id '{id}'.");
                prevOf[id] = previousId;
            }

            if (prevOf.Count == 0) return Array.Empty<string>();

            // The one true root is the migration with no predecessor (PreviousId == null). A non-null
            // PreviousId that points outside the set is a missing predecessor — that node ends up
            // unreachable and is caught by the completeness check below.
            string? root = null;
            foreach (var kvp in prevOf)
            {
                if (kvp.Value == null)
                {
                    if (root != null)
                        throw new InvalidOperationException(
                            $"Migration chain has more than one starting point ('{root}' and '{kvp.Key}'). " +
                            "Exactly one migration may have a null PreviousId.");
                    root = kvp.Key;
                }
                else
                {
                    if (nextOf.ContainsKey(kvp.Value))
                        throw new InvalidOperationException(
                            $"Migrations '{nextOf[kvp.Value]}' and '{kvp.Key}' both follow '{kvp.Value}' (forked chain).");
                    nextOf[kvp.Value] = kvp.Key;
                }
            }

            if (root == null)
                throw new InvalidOperationException(
                    "Migration chain has no starting point: every migration references a PreviousId. " +
                    "The first migration is missing, or there is a cycle.");

            var ordered = new List<string>(prevOf.Count);
            var seen = new HashSet<string>();
            for (string? cursor = root; cursor != null; cursor = nextOf.TryGetValue(cursor, out var next) ? next : null)
            {
                if (!seen.Add(cursor))
                    throw new InvalidOperationException($"Cycle detected in migration chain at '{cursor}'.");
                ordered.Add(cursor);
            }

            if (ordered.Count != prevOf.Count)
                throw new InvalidOperationException(
                    "Migration chain is broken: some migrations are not reachable from the start via PreviousId. " +
                    "A migration may be missing or its PreviousId may be wrong.");

            return ordered;
        }

        /// <summary>
        /// Resolves which migration the database is currently at from its version-history rows, honoring
        /// rollbacks. A DOWN migration records a row with <c>IsRollback = true</c> for the migration it
        /// undoes; taking the most recent row by timestamp (the old behavior) would wrongly report a
        /// rolled-back migration as current. Here a rollback removes its migration from the applied set, so
        /// the result is the newest migration that is actually applied — or <see langword="null"/> if none.
        /// </summary>
        public static string? ResolveCurrentVersion(IEnumerable<(long Id, string HumanId, DateTime AppliedAt, bool IsRollback)> records)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));

            var ordered = records.OrderBy(r => r.Id).ToList();

            var applied = ResolveAppliedSet(ordered);
            if (applied.Count == 0) return null;

            // Among currently-applied migrations, the current version is the one most recently applied — by the
            // monotonic insertion id (the true apply order), NOT AppliedAt (see ResolveAppliedSet).
            string? current = null;
            long bestId = long.MinValue;
            foreach (var r in ordered)
            {
                if (!r.IsRollback && applied.Contains(r.HumanId) && (current == null || r.Id >= bestId))
                {
                    current = r.HumanId;
                    bestId = r.Id;
                }
            }
            return current;
        }

        /// <summary>
        /// Resolves the set of HumanIds that are currently applied, honoring rollbacks: a non-rollback row
        /// adds its migration to the set and a rollback row removes it. Pure and order-independent on input
        /// (rows are sorted by the monotonic <c>Id</c> internally); never throws on valid data. Shared by
        /// <see cref="ResolveCurrentVersion"/> and the migration manager's idempotency guard.
        /// </summary>
        public static HashSet<string> ResolveAppliedSet(IEnumerable<(long Id, string HumanId, DateTime AppliedAt, bool IsRollback)> records)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));

            var applied = new HashSet<string>();
            // Order by the monotonic insertion Id (the bigint auto-increment PK = the true apply order), NOT
            // AppliedAt. AppliedAt is the app clock: two rows can tie (microsecond truncation, a coarse/virtualized
            // clock, a tight UP/DOWN re-apply) or even invert under NTP skew, and the SQL reads have no inherent
            // order — any of which would mis-net an UP/DOWN pair (leaving a rolled-back migration in the applied
            // set, or dropping an applied one). Id is immune to all of that.
            foreach (var r in records.OrderBy(r => r.Id))
            {
                if (r.IsRollback) applied.Remove(r.HumanId);
                else applied.Add(r.HumanId);
            }
            return applied;
        }
    }
#nullable disable
}
