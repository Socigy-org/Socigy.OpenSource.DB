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
    /// No-database tests for SELECT projection translation. An inline constructor must fold to one parameter
    /// (not shatter into one @p per ctor argument), and a column-dependent unsupported method call must fail
    /// fast rather than silently emit the bare column.
    /// </summary>
    [TestFixture]
    public class SelectVisitorTests
    {
        private sealed class Foo
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public Guid Gid { get; set; }
            public DateOnly D { get; set; }
            public DateTime Created { get; set; }
            public char Initial { get; set; }
        }

        private static (string Sql, NpgsqlParameter[] Parameters) Select(Expression<Func<Foo, object[]>> projection)
        {
            var command = new NpgsqlCommand();
            GetColumnName columns = name => name;
            var visitor = new PostgresqlSelectVisitor(projection.Parameters[0], columns, command);
            string sql = visitor.Parse(projection.Body);
            return (sql, command.Parameters.Cast<NpgsqlParameter>().ToArray());
        }

        [Test]
        public void PlainColumns_EmitQuotedList()
        {
            var (sql, _) = Select(x => new object[] { x.Id, x.Name });
            Assert.That(sql, Does.Contain("\"Id\""));
            Assert.That(sql, Does.Contain("\"Name\""));
        }

        // Regression: a multi-arg inline constructor shattered into "@p0@p1@p2" (invalid SQL). It must fold to one.
        [Test]
        public void InlineDateOnlyConstructor_FoldsToSingleParameter()
        {
            var (sql, ps) = Select(x => new object[] { x.Id, new DateOnly(2020, 1, 1) });
            Assert.That(ps, Has.Length.EqualTo(1), "the constructor must bind as one value, not one @p per arg");
            Assert.That(ps[0].Value, Is.EqualTo(new DateOnly(2020, 1, 1)));
            Assert.That(sql, Does.Not.Contain("@p0@p1"));
        }

        // Regression: a single-arg `new Guid("...")` bound the string, not a Guid.
        [Test]
        public void InlineGuidConstructor_BindsGuidNotString()
        {
            var g = new Guid("22222222-2222-2222-2222-222222222222");
            var (_, ps) = Select(x => new object[] { new Guid("22222222-2222-2222-2222-222222222222") });
            Assert.That(ps, Has.Length.EqualTo(1));
            Assert.That(ps[0].Value, Is.TypeOf<Guid>());
            Assert.That(ps[0].Value, Is.EqualTo(g));
        }

        // Regression: a column-dependent unsupported method call silently emitted the bare column.
        [Test]
        public void ColumnMethodCallTransform_Throws()
        {
            Assert.Throws<NotSupportedException>(() => Select(x => new object[] { x.Created.AddDays(1) }));
        }

        // Regression: a char comparison inside a projected CASE bound the int code point (65) against character(1);
        // it must bind the char value as a 1-char string.
        [Test]
        public void CharComparisonInCase_BindsCharNotCodePoint()
        {
            var (_, ps) = Select(x => new object[] { global::Socigy.OpenSource.DB.Core.SyntaxHelper.DB.Select.Case().When(x.Initial == 'A').Then(1).Else(0) });
            var values = ps.Select(p => p.Value).ToArray();
            Assert.That(values, Does.Contain("A"));
            Assert.That(values, Does.Not.Contain(65));
        }
    }
}
