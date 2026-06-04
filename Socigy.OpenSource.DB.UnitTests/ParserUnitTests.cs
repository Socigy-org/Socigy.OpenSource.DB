using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Npgsql;
using Socigy.OpenSource.DB.Core.Delegates;
using Socigy.OpenSource.DB.Core.Parsers.Postgresql;

namespace Socigy.OpenSource.DB.UnitTests
{
    /// <summary>
    /// No-database tests for the LINQ→SQL WHERE translation. They build an unopened
    /// <see cref="NpgsqlCommand"/>, run the visitor, and assert the emitted SQL/parameters — verifying
    /// the Part 3 parser hardening (escaping, nullable, arithmetic, coalesce, ILIKE) and that unsupported
    /// expressions fail fast instead of emitting invalid SQL.
    /// </summary>
    [TestFixture]
    public class ParserUnitTests
    {
        private sealed class Foo
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public bool IsActive { get; set; }
            public int? Age { get; set; }
            public decimal Amount { get; set; }
        }

        private static (string Sql, NpgsqlParameter[] Parameters) Where(Expression<Func<Foo, bool>> predicate)
        {
            var command = new NpgsqlCommand();
            GetColumnName columns = name => "\"" + name + "\"";
            var visitor = new PostgresqlWhereVisitor(predicate.Parameters[0], columns, command);
            string sql = visitor.Parse(predicate.Body);
            return (sql, command.Parameters.Cast<NpgsqlParameter>().ToArray());
        }

        [Test]
        public void Equality_EmitsParameterizedComparison()
        {
            var (sql, ps) = Where(x => x.Id == 5);
            Assert.That(sql, Does.Contain("\"Id\" = @p0"));
            Assert.That(ps, Has.Length.EqualTo(1));
            Assert.That(ps[0].Value, Is.EqualTo(5));
        }

        [Test]
        public void AndOr_NestsCorrectly()
        {
            var (sql, _) = Where(x => x.Id > 1 && x.Id < 10);
            Assert.That(sql, Does.Contain("\"Id\" > @p0"));
            Assert.That(sql, Does.Contain(" AND "));
            Assert.That(sql, Does.Contain("\"Id\" < @p1"));
        }

        [Test]
        public void Contains_EscapesLikeWildcards_AndAppendsEscapeClause()
        {
            var (sql, ps) = Where(x => x.Name.Contains("50%_off"));
            Assert.That(sql, Does.Contain("\"Name\" LIKE @p0 ESCAPE '\\'"));
            Assert.That(ps[0].Value, Is.EqualTo("%50\\%\\_off%"));
        }

        [Test]
        public void StartsWith_EmitsPrefixPattern()
        {
            var (sql, ps) = Where(x => x.Name.StartsWith("ab"));
            Assert.That(sql, Does.Contain(" LIKE @p0 ESCAPE '\\'"));
            Assert.That(ps[0].Value, Is.EqualTo("ab%"));
        }

        [Test]
        public void ToLowerContains_EmitsCaseInsensitiveILike()
        {
            var (sql, _) = Where(x => x.Name.ToLower().Contains("abc"));
            Assert.That(sql, Does.Contain("\"Name\" ILIKE @p0 ESCAPE '\\'"));
        }

        [Test]
        public void NullableHasValue_EmitsIsNotNull()
        {
            var (sql, ps) = Where(x => x.Age.HasValue);
            Assert.That(sql, Does.Contain("\"Age\" IS NOT NULL"));
            Assert.That(ps, Is.Empty);
        }

        [Test]
        public void NullableValue_TreatedAsColumn()
        {
            var (sql, _) = Where(x => x.Age!.Value > 5);
            Assert.That(sql, Does.Contain("\"Age\" > @p0"));
        }

        [Test]
        public void Coalesce_EmitsCoalesceFunction()
        {
            var (sql, _) = Where(x => (x.Age ?? 0) > 5);
            Assert.That(sql, Does.Contain("COALESCE(\"Age\", @p0)"));
            Assert.That(sql, Does.Contain(" > @p1"));
        }

        [Test]
        public void Arithmetic_EmitsInfixOperator()
        {
            var (sql, _) = Where(x => x.Id + 1 > 10);
            Assert.That(sql, Does.Contain("\"Id\" + @p0"));
        }

        [Test]
        public void IsNullOrEmpty_EmitsNullOrEmptyCheck()
        {
            var (sql, _) = Where(x => string.IsNullOrEmpty(x.Name));
            Assert.That(sql, Does.Contain("\"Name\" IS NULL OR"));
            Assert.That(sql, Does.Contain("= ''"));
        }

        [Test]
        public void NullComparison_EmitsIsNull()
        {
            var (sql, _) = Where(x => x.Name == null);
            Assert.That(sql, Does.Contain("\"Name\" IS NULL"));
        }

        [Test]
        public void UnsupportedStringMethod_Throws()
        {
            Assert.Throws<NotSupportedException>(() => Where(x => x.Name.PadLeft(3) == "abc"));
        }

        // ---- Reflection-light closure evaluation (no Expression.Compile on the hot path) ----

        [Test]
        public void CapturedLocalVariable_IsParameterized()
        {
            var name = "alice";
            var (sql, ps) = Where(x => x.Name == name);
            Assert.That(sql, Does.Contain("\"Name\" = @p0"));
            Assert.That(ps[0].Value, Is.EqualTo("alice"));
        }

        [Test]
        public void CapturedPropertyChain_IsParameterized()
        {
            var holder = new { Threshold = 10 };
            var (sql, ps) = Where(x => x.Age!.Value > holder.Threshold);
            Assert.That(sql, Does.Contain("\"Age\" > @p0"));
            Assert.That(ps[0].Value, Is.EqualTo(10));
        }

        [Test]
        public void CollectionContains_CapturedList_EmitsTypedArray()
        {
            var ids = new List<int> { 1, 2, 3 };
            var (sql, ps) = Where(x => ids.Contains(x.Id));
            Assert.That(sql, Does.Contain("\"Id\" = ANY(@p0)"));
            Assert.That(ps[0].Value, Is.AssignableTo<int[]>());
            Assert.That((int[])ps[0].Value!, Is.EqualTo(new[] { 1, 2, 3 }));
        }
    }
}
