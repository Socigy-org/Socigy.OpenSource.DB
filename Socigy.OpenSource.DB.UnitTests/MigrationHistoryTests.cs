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
        private static (string, DateTime, bool) Rec(string id, int minute, bool rollback) => (id, T0.AddMinutes(minute), rollback);

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
            Assert.That(MigrationHistory.ResolveCurrentVersion(Array.Empty<(string, DateTime, bool)>()), Is.Null);
        }
    }
}
