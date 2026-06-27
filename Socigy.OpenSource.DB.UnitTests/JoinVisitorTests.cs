using System;
using System.Linq;
using System.Linq.Expressions;
using Npgsql;
using Socigy.OpenSource.DB.Core.Delegates;
using Socigy.OpenSource.DB.Core.Parsers.Postgresql;

namespace Socigy.OpenSource.DB.UnitTests
{
    /// <summary>
    /// No-database tests for the multi-table JOIN ON/WHERE translation. The value side of a comparison must be
    /// normalized identically to the single-table WHERE path (enum→int, UTC DateTime→Unspecified, unsigned
    /// widening), and an inline constructor must fold to a single parameter instead of shattering.
    /// </summary>
    [TestFixture]
    public class JoinVisitorTests
    {
        private sealed class A
        {
            public Guid Gid { get; set; }
            public DateTime Created { get; set; }
            public uint Count { get; set; }
            public string Name { get; set; } = "";
            public char Initial { get; set; }
            public int X { get; set; }
        }
        private sealed class B { public Guid Id { get; set; } public string Label { get; set; } = ""; public int W { get; set; } }

        private static (string Sql, NpgsqlParameter[] Parameters) Where(Expression<Func<A, B, bool>> predicate)
        {
            var command = new NpgsqlCommand();
            GetColumnName cols = name => name;
            var map = new (ParameterExpression, string, GetColumnName)[]
            {
                (predicate.Parameters[0], "a0", cols),
                (predicate.Parameters[1], "a1", cols),
            };
            var visitor = new PostgresqlMultiJoinVisitor(map, command);
            string sql = visitor.Parse(predicate.Body);
            return (sql, command.Parameters.Cast<NpgsqlParameter>().ToArray());
        }

        // Regression: the join path normalized only enums, so a UTC DateTime bound with Kind=Utc and (on a naive
        // `timestamp` column) was shifted by the session time zone — wrong rows. It must relabel to Unspecified.
        [Test]
        public void UtcDateTime_NormalizedToUnspecified()
        {
            var utc = new DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc);
            var (_, ps) = Where((a, b) => a.Created > utc);
            Assert.That(ps, Has.Length.EqualTo(1));
            Assert.That(((DateTime)ps[0].Value!).Kind, Is.EqualTo(DateTimeKind.Unspecified));
        }

        // Regression: an unsigned value bound raw has no Npgsql wire mapping and throws at execution; it must widen.
        [Test]
        public void UnsignedValue_WidenedToSignedType()
        {
            uint v = 5;
            var (_, ps) = Where((a, b) => a.Count > v);
            Assert.That(ps, Has.Length.EqualTo(1));
            Assert.That(ps[0].Value, Is.TypeOf<long>(), "uint must widen to long, not bind raw");
        }

        // Regression: an inline multi-arg constructor shattered into "@p0@p1@p2" (invalid SQL).
        [Test]
        public void InlineDateTimeConstructor_FoldsToSingleParameter()
        {
            var (sql, ps) = Where((a, b) => a.Created > new DateTime(2020, 1, 1));
            Assert.That(ps, Has.Length.EqualTo(1), "the constructor must bind as one value, not one @p per arg");
            Assert.That(sql, Does.Not.Contain("@p0@p1"));
        }

        // Regression: a single-arg `new Guid("...")` bound the string, not a Guid.
        [Test]
        public void InlineGuidConstructor_BindsGuidNotString()
        {
            var g = new Guid("22222222-2222-2222-2222-222222222222");
            var (_, ps) = Where((a, b) => a.Gid == new Guid("22222222-2222-2222-2222-222222222222"));
            Assert.That(ps, Has.Length.EqualTo(1));
            Assert.That(ps[0].Value, Is.TypeOf<Guid>());
            Assert.That(ps[0].Value, Is.EqualTo(g));
        }

        // Regression (JOIN analogs of the WHERE fixes): string concat must emit `||`, a char comparison must bind
        // the char value not the int code point, and a ternary must emit CASE.
        [Test]
        public void StringConcat_InJoinPredicate_EmitsConcatOperator()
        {
            var (sql, _) = Where((a, b) => a.Name + "x" == b.Label);
            Assert.That(sql, Does.Contain(" || "));
            Assert.That(sql, Does.Not.Contain("\"Name\" + "));
        }

        [Test]
        public void CharComparison_InJoinPredicate_BindsCharNotCodePoint()
        {
            var (sql, ps) = Where((a, b) => a.Initial == 'A');
            Assert.That(sql, Does.Contain("\"Initial\" = @p0"));
            Assert.That(ps[0].Value, Is.EqualTo("A"));
            Assert.That(ps[0].Value, Is.Not.EqualTo(65));
        }

        [Test]
        public void Ternary_InJoinPredicate_EmitsCase()
        {
            var (sql, _) = Where((a, b) => (a.X > 0 ? a.X : 0) == b.W);
            Assert.That(sql, Does.Contain("CASE WHEN"));
            Assert.That(sql, Does.Contain(" THEN "));
            Assert.That(sql, Does.Contain(" END"));
        }
    }
}
