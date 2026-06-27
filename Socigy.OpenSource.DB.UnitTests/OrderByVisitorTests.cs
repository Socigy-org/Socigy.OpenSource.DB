using System;
using System.Linq;
using System.Linq.Expressions;
using Npgsql;
using Socigy.OpenSource.DB.Core.Delegates;
using Socigy.OpenSource.DB.Core.Parsers.Postgresql;
using static Socigy.OpenSource.DB.Core.SyntaxHelper.DB;

namespace Socigy.OpenSource.DB.UnitTests
{
    /// <summary>
    /// No-database tests for ORDER BY translation. A column-dependent method-call transform
    /// (e.g. <c>x.Name.ToUpper()</c>) must fail fast rather than silently emit the bare column —
    /// which ordered by the raw value, not the transform.
    /// </summary>
    [TestFixture]
    public class OrderByVisitorTests
    {
        private sealed class Foo
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public Guid Gid { get; set; }
            public DateTime Created { get; set; }
            public char Initial { get; set; }
        }

        private static string OrderBy(Expression<Func<Foo, object[]>> keys, bool descending = false)
        {
            var command = new NpgsqlCommand();
            GetColumnName columns = name => name;
            var visitor = new PostgresqlOrderByVisitor(keys.Parameters[0], columns, command, descending);
            return visitor.Parse(keys);
        }

        private static NpgsqlParameter[] OrderByParams(Expression<Func<Foo, object[]>> keys)
        {
            var command = new NpgsqlCommand();
            GetColumnName columns = name => name;
            var visitor = new PostgresqlOrderByVisitor(keys.Parameters[0], columns, command, false);
            visitor.Parse(keys);
            return command.Parameters.Cast<NpgsqlParameter>().ToArray();
        }

        [Test]
        public void PlainColumn_EmitsQuotedColumn()
        {
            var sql = OrderBy(x => new object[] { x.Id });
            Assert.That(sql, Does.Contain("ORDER BY \"Id\""));
        }

        [Test]
        public void MultiKey_Descending_RepeatsDescPerKey()
        {
            var sql = OrderBy(x => new object[] { x.Id, x.Name }, descending: true);
            Assert.That(sql, Does.Contain("\"Id\" DESC"));
            Assert.That(sql, Does.Contain("\"Name\" DESC"));
        }

        // Regression: `x.Name.ToUpper()` previously degraded to `ORDER BY "Name"` (the transform silently
        // dropped → wrong order). It must throw like the binary/operator path does for unsupported expressions.
        [Test]
        public void ColumnMethodCallTransform_Throws()
        {
            Assert.Throws<NotSupportedException>(() => OrderBy(x => new object[] { x.Name.ToUpper() }));
            Assert.Throws<NotSupportedException>(() => OrderBy(x => new object[] { x.Name.Trim() }));
        }

        // Regression: a captured value in an ORDER BY CASE (When/Then/Else) was bound raw — a UTC DateTime kept
        // Kind=Utc and (on a naive column) was shifted by the session time zone, mis-ordering rows. It must be
        // normalized to Unspecified like the WHERE/SELECT paths.
        [Test]
        public void CapturedUtcDateTimeInCase_NormalizedToUnspecified()
        {
            var utc = new DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc);
            var ps = OrderByParams(x => new object[] { Select.Case().When(x.Created > utc).Then(1).Else(2) });
            var dt = ps.Select(p => p.Value).OfType<DateTime>().Single();
            Assert.That(dt.Kind, Is.EqualTo(DateTimeKind.Unspecified));
        }

        // Regression: an inline `new Guid("...")` in an ORDER BY CASE bound the constructor's string, not a Guid.
        [Test]
        public void InlineGuidConstructorInCase_BindsGuidNotString()
        {
            var g = new Guid("22222222-2222-2222-2222-222222222222");
            var ps = OrderByParams(x => new object[]
            {
                Select.Case().When(x.Gid == new Guid("22222222-2222-2222-2222-222222222222")).Then(1).End()
            });
            Assert.That(ps.Select(p => p.Value).OfType<Guid>().Single(), Is.EqualTo(g));
        }

        // Regression: a char comparison inside an ORDER BY CASE bound the int code point (65) against character(1);
        // it must bind the char value as a 1-char string.
        [Test]
        public void CharComparisonInCase_BindsCharNotCodePoint()
        {
            var ps = OrderByParams(x => new object[] { Select.Case().When(x.Initial == 'A').Then(1).End() });
            var values = ps.Select(p => p.Value).ToArray();
            Assert.That(values, Does.Contain("A"));
            Assert.That(values, Does.Not.Contain(65));
        }
    }
}
