using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Npgsql;
using Socigy.OpenSource.DB.Core.Delegates;
using Socigy.OpenSource.DB.Core.Parsers;
using Socigy.OpenSource.DB.Core.Parsers.Postgresql;
using static Socigy.OpenSource.DB.Core.SyntaxHelper.DB;

namespace Socigy.OpenSource.DB.UnitTests
{
    /// <summary>
    /// No-database tests for the query-shape cache: the structural hash (<see cref="ExpressionStructure"/>),
    /// the value-only parameter binding (<c>BindParameters</c>), and the proof that a cache hit produces
    /// SQL + parameters identical to a fresh translation.
    /// </summary>
    [TestFixture]
    public class QueryShapeCacheTests
    {
        private sealed class Foo
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public bool IsActive { get; set; }
            public int? Age { get; set; }
        }

        private static readonly GetColumnName Columns = name => "\"" + name + "\"";

        private static long Hash(Expression<Func<Foo, bool>> p)
        {
            Assert.That(ExpressionStructure.TryComputeHash(p.Body, p.Parameters[0], out long h), Is.True,
                "expected a cacheable shape");
            return h;
        }

        private static (string Sql, NpgsqlParameter[] Ps) Parse(Expression<Func<Foo, bool>> p)
        {
            var cmd = new NpgsqlCommand();
            string sql = new PostgresqlWhereVisitor(p.Parameters[0], Columns, cmd).Parse(p.Body);
            return (sql, cmd.Parameters.Cast<NpgsqlParameter>().ToArray());
        }

        private static NpgsqlParameter[] Bind(Expression<Func<Foo, bool>> p)
        {
            var cmd = new NpgsqlCommand();
            new PostgresqlWhereVisitor(p.Parameters[0], Columns, cmd).BindParameters(p.Body);
            return cmd.Parameters.Cast<NpgsqlParameter>().ToArray();
        }

        // ---- Structural hash ----------------------------------------------------------------

        [Test]
        public void SameShape_DifferentValues_ProduceSameHash()
        {
            Assert.That(Hash(x => x.Id == 5), Is.EqualTo(Hash(x => x.Id == 9)));
        }

        [Test]
        public void SameShape_CapturedVariables_ProduceSameHash()
        {
            int a = 1, b = 2;
            Assert.That(Hash(x => x.Id == a), Is.EqualTo(Hash(x => x.Id == b)));
            // captured and literal both collapse to a value slot
            Assert.That(Hash(x => x.Id == a), Is.EqualTo(Hash(x => x.Id == 7)));
        }

        [Test]
        public void DifferentOperator_ProducesDifferentHash()
        {
            Assert.That(Hash(x => x.Id < 5), Is.Not.EqualTo(Hash(x => x.Id > 5)));
        }

        [Test]
        public void DifferentColumn_ProducesDifferentHash()
        {
            Assert.That(Hash(x => x.Id == 5), Is.Not.EqualTo(Hash(x => x.Age == 5)));
        }

        [Test]
        public void NullLiteral_DiffersFromValueComparison()
        {
            Assert.That(Hash(x => x.Name == null), Is.Not.EqualTo(Hash(x => x.Name == "x")));
        }

        [Test]
        public void DifferentStringMethod_ProducesDifferentHash()
        {
            Assert.That(Hash(x => x.Name.Contains("a")), Is.Not.EqualTo(Hash(x => x.Name.StartsWith("a"))));
            // ToLower wrapping (ILIKE) differs from the plain form (LIKE)
            Assert.That(Hash(x => x.Name.ToLower().Contains("a")), Is.Not.EqualTo(Hash(x => x.Name.Contains("a"))));
        }

        [Test]
        public void CustomRawSql_IsUncacheable()
        {
            Expression<Func<Foo, bool>> p = x => Query.Custom("1 = 1");
            Assert.That(ExpressionStructure.TryComputeHash(p.Body, p.Parameters[0], out _), Is.False);
        }

        // ---- Value-only binding -------------------------------------------------------------

        [Test]
        public void BindParameters_ExtractsSameValues_AsParse()
        {
            Expression<Func<Foo, bool>> p = x => x.Id > 1 && x.Name.Contains("ab");
            var (_, parsed) = Parse(p);
            var bound = Bind(p);

            Assert.That(bound.Select(x => x.ParameterName), Is.EqualTo(parsed.Select(x => x.ParameterName)));
            Assert.That(bound.Select(x => x.Value), Is.EqualTo(parsed.Select(x => x.Value)));
        }

        // ---- End-to-end: a cache hit equals a fresh translation -----------------------------

        [Test]
        public void CacheHit_ReusedSql_PlusBind_EqualsFreshTranslation()
        {
            // Shape is translated once for value 5, then reused for value 9.
            Expression<Func<Foo, bool>> first = x => x.Id == 5;
            Expression<Func<Foo, bool>> second = x => x.Id == 9;

            Assert.That(Hash(first), Is.EqualTo(Hash(second)), "same shape must share a cache key");

            var (cachedSql, _) = Parse(first);     // what the cache would store
            var boundForSecond = Bind(second);     // what a cache hit re-binds

            var (freshSql, freshPs) = Parse(second); // ground truth (no cache)

            Assert.That(cachedSql, Is.EqualTo(freshSql));
            Assert.That(boundForSecond.Select(x => x.ParameterName), Is.EqualTo(freshPs.Select(x => x.ParameterName)));
            Assert.That(boundForSecond.Select(x => x.Value), Is.EqualTo(freshPs.Select(x => x.Value)));
            Assert.That(boundForSecond.Single().Value, Is.EqualTo(9));
        }

        // ---- Cache store --------------------------------------------------------------------

        [Test]
        public void Cache_AddThenGet_RoundTrips()
        {
            QueryShapeCache.Clear();
            QueryShapeCache.Add(typeof(Foo), 12345L, " WHERE \"Id\" = @p0", 1);

            Assert.That(QueryShapeCache.TryGet(typeof(Foo), 12345L, out string sql, out int count), Is.True);
            Assert.That(sql, Is.EqualTo(" WHERE \"Id\" = @p0"));
            Assert.That(count, Is.EqualTo(1));

            // Different type partitions the key.
            Assert.That(QueryShapeCache.TryGet(typeof(string), 12345L, out _, out _), Is.False);
            QueryShapeCache.Clear();
        }
    }
}
