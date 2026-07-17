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
            public Guid Gid { get; set; }
            public DateOnly D { get; set; }
            public char Initial { get; set; }
            public string Last { get; set; } = "";
        }

        private static (string Sql, NpgsqlParameter[] Parameters) Where(Expression<Func<Foo, bool>> predicate)
        {
            var command = new NpgsqlCommand();
            // Mirror production: GetColumnDbName returns the bare column name and the visitor quotes it.
            GetColumnName columns = name => name;
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

        // Regression: `string + string` compiles to a BinaryExpression Add, which emitted SQL `+` ("operator does
        // not exist: text + text"); it must emit `||`.
        [Test]
        public void StringConcat_EmitsSqlConcatOperator()
        {
            var (sql, _) = Where(x => x.Name + x.Last == "ab");
            Assert.That(sql, Does.Contain("\"Name\" || \"Last\""));
            Assert.That(sql, Does.Not.Contain("\"Name\" + "));
        }

        // Regression: a char comparison promotes to int==int and bound the code point (65) against the
        // character(1) column ("character(1) = integer"); it must bind the char value as a 1-char string.
        [Test]
        public void CharComparison_BindsCharValueNotCodePoint()
        {
            var (sql, ps) = Where(x => x.Initial == 'A');
            Assert.That(sql, Does.Contain("\"Initial\" = @p0"));
            Assert.That(ps[0].Value, Is.EqualTo("A"));
            Assert.That(ps[0].Value, Is.Not.EqualTo(65));
        }

        // Regression: a ternary in a predicate had no VisitConditional override → malformed SQL; it must emit CASE.
        [Test]
        public void Ternary_EmitsCaseExpression()
        {
            var (sql, _) = Where(x => (x.Id > 0 ? x.Amount : 0m) > 5m);
            Assert.That(sql, Does.Contain("CASE WHEN"));
            Assert.That(sql, Does.Contain(" THEN "));
            Assert.That(sql, Does.Contain(" ELSE "));
            Assert.That(sql, Does.Contain(" END"));
        }

        // An inline multi-arg constructor on the value side must fold to ONE parameter, not shatter into one
        // @p per ctor argument (which produced broken SQL like "@p0@p1@p2" → PostgreSQL syntax error).
        [Test]
        public void InlineDateOnlyConstructor_FoldsToSingleParameter()
        {
            var (sql, ps) = Where(x => x.D > new DateOnly(2020, 1, 1));
            Assert.That(sql, Does.Contain("\"D\" > @p0"));
            Assert.That(ps, Has.Length.EqualTo(1), "the constructor must bind as one value, not one @p per arg");
            Assert.That(ps[0].Value, Is.EqualTo(new DateOnly(2020, 1, 1)));
        }

        // A single-arg constructor was syntactically valid but bound the wrong CLR type — `new Guid("...")`
        // bound the String, yielding a live `operator does not exist: uuid = text`. It must bind a Guid.
        [Test]
        public void InlineGuidConstructor_BindsGuidNotString()
        {
            var g = new Guid("22222222-2222-2222-2222-222222222222");
            var (sql, ps) = Where(x => x.Gid == new Guid("22222222-2222-2222-2222-222222222222"));
            Assert.That(sql, Does.Contain("\"Gid\" = @p0"));
            Assert.That(ps, Has.Length.EqualTo(1));
            Assert.That(ps[0].Value, Is.EqualTo(g));
            Assert.That(ps[0].Value, Is.TypeOf<Guid>(), "must bind a Guid, not the String constructor argument");
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

        // ── != over a NON-nullable column ───────────────────────────────────────────
        // Reported as "!= matches no rows" for a Guid column. It translates to a plain <>: the
        // "OR col IS NULL" widening is gated on the column being Nullable<T>, and that widening could only
        // ever ADD rows anyway, never empty a result set.
        [Test]
        public void NotEqual_NonNullableGuid_EmitsPlainNotEquals()
        {
            var g = Guid.NewGuid();
            var (sql, ps) = Where(x => x.Gid != g);
            Assert.That(sql, Does.Contain("\"Gid\" <> @p0"));
            Assert.That(sql, Does.Not.Contain("IS NULL"), "a non-nullable column must not get the nullable widening");
            Assert.That(ps, Has.Length.EqualTo(1));
            Assert.That(ps[0].Value, Is.EqualTo(g));
            Assert.That(ps[0].Value, Is.TypeOf<Guid>(), "a Guid must bind as uuid, not as text");
        }

        [Test]
        public void NotEqual_CombinedWithEquality_EmitsBothClauses()
        {
            var g = Guid.NewGuid();
            var (sql, ps) = Where(x => x.Id == 1 && x.Gid != g);
            Assert.That(sql, Does.Contain("\"Id\" = @p0"));
            Assert.That(sql, Does.Contain(" AND "));
            Assert.That(sql, Does.Contain("\"Gid\" <> @p1"), "the inequality must survive alongside the equality key");
            Assert.That(sql, Does.Not.Contain("IS NULL"));
            Assert.That(ps, Has.Length.EqualTo(2));
        }

        [Test]
        public void NotEqual_NullableColumn_WidensToIncludeNulls()
        {
            // The counterpart: a nullable column DOES get the widening, because in C# `null != 5` is true.
            int? age = 5;
            var (sql, _) = Where(x => x.Age != age);
            Assert.That(sql, Does.Contain("\"Age\" <> @p0"));
            Assert.That(sql, Does.Contain("\"Age\" IS NULL"));
        }

        // ── Coalesce with a null literal ────────────────────────────────────────────
        // The literal-null -> IS NULL / IS NOT NULL rewrite used to run for ANY node type, so a Coalesce
        // whose right side is null emitted "col IS NOT NULL" (a boolean) where a value belongs.
        [Test]
        public void Coalesce_WithNullLiteral_EmitsCoalesceNotIsNotNull()
        {
            var (sql, _) = Where(x => (x.Name ?? null) == "a");
            Assert.That(sql, Does.Contain("COALESCE"), "?? must stay a COALESCE");
            Assert.That(sql, Does.Not.Contain("IS NOT NULL"),
                "the null-literal rewrite must be gated to == / != only");
        }
    }
}
