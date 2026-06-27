using System;
using System.Collections.Generic;
using System.Linq;
using Socigy.OpenSource.DB.Core.Migrations;

namespace Socigy.OpenSource.DB.UnitTests
{
    /// <summary>
    /// Migration apply order must come from the PreviousId chain, not from sorting ids. Ids are
    /// minute-granularity timestamps (and may be user-named), so id-sorting is unreliable.
    /// </summary>
    [TestFixture]
    public class MigrationHistoryTests
    {
        private static IReadOnlyList<string> Order(params (string, string?)[] m) => MigrationHistory.OrderByChain(m);

        [Test]
        public void Orders_by_chain_regardless_of_input_order()
        {
            var ordered = Order(("c", "b"), ("a", null), ("b", "a"));
            Assert.That(ordered, Is.EqualTo(new[] { "a", "b", "c" }));
        }

        [Test]
        public void Order_follows_chain_even_when_ids_do_not_sort_lexicographically()
        {
            // Lexicographic order would be [aaa, mmm, zzz]; the real chain is zzz -> aaa -> mmm.
            var ordered = Order(("aaa", "zzz"), ("mmm", "aaa"), ("zzz", null));
            Assert.That(ordered, Is.EqualTo(new[] { "zzz", "aaa", "mmm" }));
        }

        [Test]
        public void Same_minute_timestamp_ids_are_ordered_by_chain_not_by_string()
        {
            // Two migrations created in the same minute share a timestamp prefix; only the chain disambiguates.
            var ordered = Order(
                ("202601011200_b_222", "202601011200_a_111"),
                ("202601011200_a_111", null));
            Assert.That(ordered, Is.EqualTo(new[] { "202601011200_a_111", "202601011200_b_222" }));
        }

        [Test]
        public void Empty_input_returns_empty()
        {
            Assert.That(Order(), Is.Empty);
        }

        [Test]
        public void Broken_chain_missing_predecessor_throws()
        {
            // 'b' references a predecessor 'a' that is not present.
            Assert.Throws<InvalidOperationException>(() => Order(("b", "a"), ("c", "b")));
        }

        [Test]
        public void Forked_chain_throws()
        {
            // Both 'b' and 'c' claim 'a' as predecessor.
            Assert.Throws<InvalidOperationException>(() => Order(("a", null), ("b", "a"), ("c", "a")));
        }

        [Test]
        public void Two_roots_throws()
        {
            Assert.Throws<InvalidOperationException>(() => Order(("a", null), ("b", null)));
        }

        [Test]
        public void Duplicate_id_throws()
        {
            Assert.Throws<InvalidOperationException>(() => Order(("a", null), ("a", null)));
        }

        // ── ResolveCurrentVersion (rollback-aware) ──────────────────────────────────
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // The `minute` doubles as the monotonic insertion Id (apply order) AND the AppliedAt offset.
        private static (long, string, DateTime, bool) Rec(string id, int minute, bool rollback) => (minute, id, T0.AddMinutes(minute), rollback);

        [Test]
        public void Current_version_is_the_latest_applied()
        {
            var current = MigrationHistory.ResolveCurrentVersion(new[] { Rec("a", 1, false), Rec("b", 2, false) });
            Assert.That(current, Is.EqualTo("b"));
        }

        [Test]
        public void Rolled_back_migration_is_not_reported_as_current()
        {
            // Apply a, apply b, then roll back b -> current must be a (the old max-AppliedAt logic returned b).
            var current = MigrationHistory.ResolveCurrentVersion(new[]
            {
                Rec("a", 1, false), Rec("b", 2, false), Rec("b", 3, true),
            });
            Assert.That(current, Is.EqualTo("a"));
        }

        [Test]
        public void Reapplied_migration_is_current_again()
        {
            var current = MigrationHistory.ResolveCurrentVersion(new[]
            {
                Rec("a", 1, false), Rec("a", 2, true), Rec("a", 3, false),
            });
            Assert.That(current, Is.EqualTo("a"));
        }

        [Test]
        public void All_rolled_back_yields_null()
        {
            var current = MigrationHistory.ResolveCurrentVersion(new[] { Rec("a", 1, false), Rec("a", 2, true) });
            Assert.That(current, Is.Null);
        }

        [Test]
        public void No_history_yields_null()
        {
            Assert.That(MigrationHistory.ResolveCurrentVersion(Array.Empty<(long, string, DateTime, bool)>()), Is.Null);
        }

        // ── ResolveAppliedSet (drives the migration manager's UP-loop idempotency guard) ──
        [Test]
        public void Applied_set_contains_every_non_rolled_back_migration()
        {
            var applied = MigrationHistory.ResolveAppliedSet(new[] { Rec("a", 1, false), Rec("b", 2, false) });
            Assert.That(applied, Is.EquivalentTo(new[] { "a", "b" }));
        }

        [Test]
        public void Applied_set_excludes_a_rolled_back_migration()
        {
            // UP a, UP b, DOWN b -> only a is still applied.
            var applied = MigrationHistory.ResolveAppliedSet(new[]
            {
                Rec("a", 1, false), Rec("b", 2, false), Rec("b", 3, true),
            });
            Assert.That(applied, Is.EquivalentTo(new[] { "a" }));
        }

        [Test]
        public void Applied_set_includes_a_migration_that_was_rolled_back_then_reapplied()
        {
            // UP a, DOWN a, UP a -> a is applied again. This is the case where a naive
            // "a non-rollback row exists" guard would wrongly skip re-applying a's UP DDL.
            var applied = MigrationHistory.ResolveAppliedSet(new[]
            {
                Rec("a", 1, false), Rec("a", 2, true), Rec("a", 3, false),
            });
            Assert.That(applied, Does.Contain("a"));
        }

        [Test]
        public void Applied_set_is_empty_when_nothing_is_applied()
        {
            Assert.That(MigrationHistory.ResolveAppliedSet(Array.Empty<(long, string, DateTime, bool)>()), Is.Empty);
        }

        // Regression: the resolver must net UP/DOWN by the monotonic insertion Id, NOT AppliedAt. With an AppliedAt
        // TIE (same instant) and the rows supplied in reverse (DOWN before UP), ordering by AppliedAt kept the input
        // order (DOWN then UP -> Remove then Add -> wrongly "applied"). Ordering by Id (UP=1, DOWN=2) nets correctly.
        [Test]
        public void Applied_set_nets_by_id_not_appliedat_on_a_tie()
        {
            var sameInstant = T0;
            var records = new (long, string, DateTime, bool)[]
            {
                (2, "a", sameInstant, true),    // DOWN — higher id, supplied FIRST
                (1, "a", sameInstant, false),   // UP   — lower id, supplied second
            };
            Assert.That(MigrationHistory.ResolveAppliedSet(records), Is.Empty,
                "UP(id 1) then DOWN(id 2) nets to not-applied regardless of input/AppliedAt order");
            Assert.That(MigrationHistory.ResolveCurrentVersion(records), Is.Null);
        }

        [Test]
        public void Down_guard_skips_a_migration_that_is_no_longer_applied()
        {
            // The migration manager's DOWN loop now guards with `applied.Contains(id)` (mirroring the UP loop), so
            // a concurrent/stale rollback cannot re-run DownSql for an already-rolled-back migration. This asserts
            // the decision the guard makes: after UP a, UP b, DOWN b, a second DOWN of b must be skipped (b is not
            // in the applied set), while a (still applied) would be rolled back.
            var applied = MigrationHistory.ResolveAppliedSet(new[]
            {
                Rec("a", 1, false), Rec("b", 2, false), Rec("b", 3, true),
            });
            Assert.That(applied.Contains("b"), Is.False, "an already-rolled-back migration must be skipped by the DOWN guard");
            Assert.That(applied.Contains("a"), Is.True, "a still-applied migration is eligible for rollback");
        }
    }
}
