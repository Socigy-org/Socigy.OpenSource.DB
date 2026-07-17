using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Npgsql;
using Socigy.OpenSource.DB.Core.CommandBuilders;
using Socigy.OpenSource.DB.Core.Delegates;
using Socigy.OpenSource.DB.Core.Interfaces;
using Socigy.OpenSource.DB.Core.Parsers;
using Socigy.OpenSource.DB.Core.Parsers.Delegates;
using Socigy.OpenSource.DB.Core.Parsers.Postgresql;

namespace Socigy.OpenSource.DB.UnitTests
{
    /// <summary>
    /// The AOT-safe string Select/OrderBy overloads emit column names straight into SQL. A name that does not
    /// resolve to a known column is taken to be a DB name and quoted as-is — so any embedded double-quote MUST be
    /// doubled, or it would break out of the quoted identifier (a SQL-injection vector for a dynamically supplied name).
    /// </summary>
    [TestFixture]
    public class AotNamedColumnSqlTests
    {
        private sealed class Foo : IDbTable
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";

            public string GetTableName() => "foo";
            public Dictionary<string, ColumnInfo> GetColumns() => new();
            public Dictionary<string, ColumnInfo> GetPrimaryColumns() => new();
            public (string Name, ColumnInfo Info)? GetColumn(string name) => null;
            public string? GetDbColumnName(string memberName)
                => memberName == nameof(Id) ? "id" : memberName == nameof(Name) ? "name" : null;
        }

        private static SqlQueryBuilderExpressionParser<Foo> NewParser(NpgsqlCommand cmd)
        {
            GetColumnName cols = name => name == nameof(Foo.Id) ? "id" : name == nameof(Foo.Name) ? "name" : null!;
            CreateSelectVisitor sel = (p, g, c) => new PostgresqlSelectVisitor(p, g, c);
            CreateWhereVisitor whr = (p, g, c) => new PostgresqlWhereVisitor(p, g, c);
            CreateOrderByVisitor ord = (p, g, c, d) => new PostgresqlOrderByVisitor(p, g, c, d);
            return new SqlQueryBuilderExpressionParser<Foo>(cmd, cols, sel, whr, ord);
        }

        [Test]
        public void Select_ResolvedName_IsQuotedDbColumn()
        {
            using var cmd = new NpgsqlCommand();
            Expression<Func<Foo, bool>> where = x => x.Id == 1;
            string sql = NewParser(cmd).BuildCommand("foo", null, where, null, false, -1, 0,
                selectColumns: new[] { nameof(Foo.Name) }, orderByColumns: null);
            Assert.That(sql, Does.Contain("SELECT \"name\""), "property name resolves to its DB column, quoted");
        }

        [Test]
        public void Select_EmbeddedQuote_IsDoubled()
        {
            using var cmd = new NpgsqlCommand();
            Expression<Func<Foo, bool>> where = x => x.Id == 1;
            // An unresolved raw name containing a quote-breakout attempt.
            string sql = NewParser(cmd).BuildCommand("foo", null, where, null, false, -1, 0,
                selectColumns: new[] { "x\" FROM secret; --" }, orderByColumns: null);
            Assert.That(sql, Does.Contain("\"x\"\" FROM secret; --\""),
                "embedded double-quote must be doubled so it cannot break out of the identifier");
            Assert.That(sql, Does.Not.Contain("\"x\" FROM secret"), "must not emit an unescaped breakout");
        }

        [Test]
        public void Select_AllEmptyNames_FallsBackToStar()
        {
            using var cmd = new NpgsqlCommand();
            Expression<Func<Foo, bool>> where = x => x.Id == 1;
            // An all-empty/whitespace projection must fall back to "*" rather than emitting "SELECT  FROM".
            string sql = NewParser(cmd).BuildCommand("foo", null, where, null, false, -1, 0,
                selectColumns: new[] { "", "" }, orderByColumns: null);
            Assert.That(sql, Does.Contain("SELECT * "));
            Assert.That(sql, Does.Not.Contain("SELECT  FROM"));
        }

        [Test]
        public void OrderBy_AllEmptyNames_EmitsNoOrderBy()
        {
            using var cmd = new NpgsqlCommand();
            Expression<Func<Foo, bool>> where = x => x.Id == 1;
            string sql = NewParser(cmd).BuildCommand("foo", null, where, null, false, -1, 0,
                selectColumns: null, orderByColumns: new[] { "", "" });
            Assert.That(sql, Does.Not.Contain("ORDER BY"), "an all-empty order-by must not emit a dangling ORDER BY");
        }

        [Test]
        public void OrderByDesc_AppliesDescPerColumn_AndEscapes()
        {
            using var cmd = new NpgsqlCommand();
            Expression<Func<Foo, bool>> where = x => x.Id == 1;
            string sql = NewParser(cmd).BuildCommand("foo", null, where, null, true, -1, 0,
                selectColumns: null, orderByColumns: new[] { nameof(Foo.Id), nameof(Foo.Name) });
            Assert.That(sql, Does.Contain("\"id\" DESC, \"name\" DESC"), "DESC must apply to every column");
        }
    }
}
