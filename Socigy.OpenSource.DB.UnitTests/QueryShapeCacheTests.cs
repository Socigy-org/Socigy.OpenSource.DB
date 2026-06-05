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
    /// No-database tests for the compiled-query cache: the structural hash (<see cref="ExpressionStructure"/>),
    /// and the proof that replaying a recorded parameter plan over a structurally identical tree reproduces
    /// exactly the parameters a fresh translation would produce — for every supported predicate shape.
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

        private static (string Sql, NpgsqlParameter[] Ps, IReadOnlyList<RecordedParameter> Rec) Translate(Expression<Func<Foo, bool>> p)
        {
            var cmd = new NpgsqlCommand();
            var v = new PostgresqlWhereVisitor(p.Parameters[0], Columns, cmd);
            string sql = v.Parse(p.Body);
            return (sql, cmd.Parameters.Cast<NpgsqlParameter>().ToArray(), ((IParameterRecorder)v).RecordedParameters);
        }

        /// <summary>Replays a plan recorded from <paramref name="planBody"/> against <paramref name="targetBody"/>.</summary>
        private static object?[] Replay(Expression planBody, IReadOnlyList<RecordedParameter> rec, Expression targetBody)
        {
            var values = new object?[rec.Count];
            for (int i = 0; i < rec.Count; i++)
            {
                int[]? path = ExpressionPath.ComputePath(planBody, rec[i].Source);
                Assert.That(path, Is.Not.Null, "source must be locatable in the tree");
                Expression node = ExpressionPath.Navigate(targetBody, path!);
                values[i] = WhereParameter.Apply(rec[i].Transform, ExpressionEvaluator.Evaluate(node), rec[i].ArrayElementType);
            }
            return values;
        }

        /// <summary>The core guarantee: a cache hit (replay over a new tree) == a fresh translation.</summary>
        private static void AssertReplayMatchesFresh(Expression<Func<Foo, bool>> first, Expression<Func<Foo, bool>> second)
        {
            Assert.That(Hash(first), Is.EqualTo(Hash(second)), "same shape must share a key");

            var a = Translate(first);              // the "miss" that populates the cache
            var b = Translate(second);             // ground truth for the second value
            var replayed = Replay(first.Body, a.Rec, second.Body); // what a cache hit would bind

            Assert.That(a.Sql, Is.EqualTo(b.Sql), "cached SQL must equal a fresh translation");
            Assert.That(replayed.Length, Is.EqualTo(b.Ps.Length), "param count must match");
            Assert.That(replayed, Is.EqualTo(b.Ps.Select(p => p.Value)), "replayed values must match a fresh bind");
        }

        // ---- Structural hash ----------------------------------------------------------------

        [Test]
        public void SameShape_DifferentValues_ProduceSameHash()
            => Assert.That(Hash(x => x.Id == 5), Is.EqualTo(Hash(x => x.Id == 9)));

        [Test]
        public void DifferentOperator_ProducesDifferentHash()
            => Assert.That(Hash(x => x.Id < 5), Is.Not.EqualTo(Hash(x => x.Id > 5)));

        [Test]
        public void DifferentColumn_ProducesDifferentHash()
            => Assert.That(Hash(x => x.Id == 5), Is.Not.EqualTo(Hash(x => x.Age == 5)));

        [Test]
        public void NullLiteral_DiffersFromValueComparison()
            => Assert.That(Hash(x => x.Name == null), Is.Not.EqualTo(Hash(x => x.Name == "x")));

        [Test]
        public void DifferentStringMethod_ProducesDifferentHash()
        {
            Assert.That(Hash(x => x.Name.Contains("a")), Is.Not.EqualTo(Hash(x => x.Name.StartsWith("a"))));
            Assert.That(Hash(x => x.Name.ToLower().Contains("a")), Is.Not.EqualTo(Hash(x => x.Name.Contains("a"))));
        }

        [Test]
        public void CustomRawSql_IsUncacheable()
        {
            Expression<Func<Foo, bool>> p = x => Query.Custom("1 = 1");
            Assert.That(ExpressionStructure.TryComputeHash(p.Body, p.Parameters[0], out _), Is.False);
        }

        // ---- Replay correctness across shapes -----------------------------------------------

        [Test]
        public void Replay_Equality() => AssertReplayMatchesFresh(x => x.Id == 5, x => x.Id == 9);

        [Test]
        public void Replay_AndPlusLike()
            => AssertReplayMatchesFresh(x => x.Id > 1 && x.Name.Contains("ab"), x => x.Id > 7 && x.Name.Contains("zzz%"));

        [Test]
        public void Replay_StartsWith_CaseInsensitive()
            => AssertReplayMatchesFresh(x => x.Name.ToLower().StartsWith("a"), x => x.Name.ToLower().StartsWith("qq"));

        [Test]
        public void Replay_Coalesce()
            => AssertReplayMatchesFresh(x => (x.Age ?? 0) > 5, x => (x.Age ?? 0) > 42);

        [Test]
        public void Replay_NullableHasValue_NoParameters()
            => AssertReplayMatchesFresh(x => x.Age.HasValue, x => x.Age.HasValue);

        [Test]
        public void Replay_CollectionContains_TypedArray()
        {
            var first = new List<int> { 1, 2, 3 };
            var second = new List<int> { 9, 8 };
            Expression<Func<Foo, bool>> p1 = x => first.Contains(x.Id);
            Expression<Func<Foo, bool>> p2 = x => second.Contains(x.Id);

            var a = Translate(p1);
            var b = Translate(p2);
            var replayed = Replay(p1.Body, a.Rec, p2.Body);

            Assert.That(a.Sql, Is.EqualTo(b.Sql));
            Assert.That(replayed.Single(), Is.AssignableTo<int[]>());
            Assert.That((int[])replayed.Single()!, Is.EqualTo(new[] { 9, 8 }));
        }

        // ---- Cache store --------------------------------------------------------------------

        [Test]
        public void Cache_AddThenGet_RoundTrips()
        {
            QueryShapeCache.Clear();
            var plan = new[] { new ParamSlot(new[] { 1 }, ParamTransform.Value, null) };
            QueryShapeCache.Add(typeof(Foo), 12345L, new CompiledQuery(" WHERE \"Id\" = @p0", plan));

            Assert.That(QueryShapeCache.TryGet(typeof(Foo), 12345L, out CompiledQuery q), Is.True);
            Assert.That(q.Sql, Is.EqualTo(" WHERE \"Id\" = @p0"));
            Assert.That(q.Plan, Has.Length.EqualTo(1));
            Assert.That(QueryShapeCache.TryGet(typeof(string), 12345L, out _), Is.False);
            QueryShapeCache.Clear();
        }
    }
}
