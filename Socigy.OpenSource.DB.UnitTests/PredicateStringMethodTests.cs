using System;
using System.Linq;
using System.Linq.Expressions;
using Npgsql;
using Socigy.OpenSource.DB.Core.Delegates;
using Socigy.OpenSource.DB.Core.Parsers.Postgresql;

namespace Socigy.OpenSource.DB.UnitTests
{
    /// <summary>
    /// No-database tests for string-method translation in the WHERE and SELECT visitors:
    /// the SELECT visitor must emit LOWER/UPPER (not silently drop the transform) and throw on an unsupported
    /// method, and both visitors must fail fast on a null LIKE/Equals argument (which would match every row).
    /// </summary>
    [TestFixture]
    public class PredicateStringMethodTests
    {
        private sealed class Foo
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
        }

        private static GetColumnName Columns => name => name;

        private static string Where(Expression<Func<Foo, bool>> p)
        {
            var cmd = new NpgsqlCommand();
            return new PostgresqlWhereVisitor(p.Parameters[0], Columns, cmd).Parse(p.Body);
        }

        private static string Select(Expression<Func<Foo, object?>> p)
        {
            var cmd = new NpgsqlCommand();
            Expression body = p.Body;
            if (body is UnaryExpression u && u.NodeType == ExpressionType.Convert) body = u.Operand;
            return new PostgresqlSelectVisitor(p.Parameters[0], Columns, cmd).Parse(body);
        }

        // ── Bug 4: SELECT visitor handles ToLower/ToUpper and rejects unknown methods ──

        [Test]
        public void Select_ToLower_EmitsLower()
            => Assert.That(Select(x => x.Name.ToLower()), Does.Contain("LOWER(").And.Contain("\"Name\""));

        [Test]
        public void Select_ToUpper_EmitsUpper()
            => Assert.That(Select(x => x.Name.ToUpper()), Does.Contain("UPPER(").And.Contain("\"Name\""));

        [Test]
        public void Select_UnsupportedStringMethod_Throws()
        {
            // Previously fell through and emitted just the bare column, silently dropping the transform.
            Assert.Throws<NotSupportedException>(() => Select(x => x.Name.Trim()));
        }

        [Test]
        public void Select_Contains_StillEmitsLike()
            => Assert.That(Select(x => x.Name.Contains("a")), Does.Contain(" LIKE "));

        // ── Bug 5: null LIKE/Equals argument fails fast (would otherwise match every row) ──

        [Test]
        public void Where_ContainsNull_Throws()
        {
            string? s = null;
            Assert.Throws<ArgumentNullException>(() => Where(x => x.Name.Contains(s!)));
        }

        [Test]
        public void Where_StartsWithNull_Throws()
        {
            string? s = null;
            Assert.Throws<ArgumentNullException>(() => Where(x => x.Name.StartsWith(s!)));
        }

        [Test]
        public void Where_EqualsNull_Throws()
        {
            string? s = null;
            Assert.Throws<ArgumentNullException>(() => Where(x => x.Name.Equals(s!)));
        }

        [Test]
        public void Select_ContainsNull_Throws()
        {
            string? s = null;
            Assert.Throws<ArgumentNullException>(() => Select(x => x.Name.Contains(s!)));
        }

        [Test]
        public void Where_ContainsNonNull_StillWorks()
            => Assert.That(Where(x => x.Name.Contains("a")), Does.Contain(" LIKE "));

        // StartsWith/EndsWith/Contains with a StringComparison.*IgnoreCase argument must emit ILIKE (case-
        // insensitive), mirroring the Equals path — it previously silently emitted case-sensitive LIKE.
        [Test]
        public void Where_StartsWith_IgnoreCase_EmitsILike()
        {
            Assert.That(Where(x => x.Name.StartsWith("a", StringComparison.OrdinalIgnoreCase)), Does.Contain(" ILIKE "));
            Assert.That(Where(x => x.Name.Contains("a", StringComparison.OrdinalIgnoreCase)), Does.Contain(" ILIKE "));
            Assert.That(Where(x => x.Name.EndsWith("a", StringComparison.InvariantCultureIgnoreCase)), Does.Contain(" ILIKE "));
        }

        [Test]
        public void Where_StartsWith_CaseSensitiveComparison_EmitsLike()
            => Assert.That(Where(x => x.Name.StartsWith("a", StringComparison.Ordinal)),
                Does.Contain(" LIKE ").And.Not.Contain(" ILIKE "));
    }
}
