using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Checks;

namespace UnitTest.DB.Tests;

/// <summary>
/// Tests for <see cref="DbCheck"/> expression builder, <see cref="DbCheckExpr"/>,
/// and the <see cref="IDbCheckExpression"/> contract.
/// These are pure unit tests — no database connection is required.
/// </summary>
[TestFixture]
public class DbCheckTests
{
    // ------------------------------------------------------------------
    // DbCheck.Value / DbCheck.Column
    // ------------------------------------------------------------------

    [Test]
    public void Value_CamelCase_ConvertsToSnakeCaseAndQuotes()
    {
        var expr = DbCheck.Value("PhoneNumber");
        Assert.That(expr.Sql, Is.EqualTo("\"phone_number\""));
    }

    [Test]
    public void Value_AllLowercase_QuotesAsIs()
    {
        var expr = DbCheck.Value("email");
        Assert.That(expr.Sql, Is.EqualTo("\"email\""));
    }

    [Test]
    public void Value_PascalSingleWord_ConvertsToLowercase()
    {
        var expr = DbCheck.Value("Tag");
        Assert.That(expr.Sql, Is.EqualTo("\"tag\""));
    }

    [Test]
    public void Column_AlreadySnakeCase_QuotesDirectly()
    {
        var expr = DbCheck.Column("first_name");
        Assert.That(expr.Sql, Is.EqualTo("\"first_name\""));
    }

    // ------------------------------------------------------------------
    // DbCheck.Operators
    // ------------------------------------------------------------------

    [TestCase("<")]
    [TestCase(">")]
    [TestCase("<=")]
    [TestCase(">=")]
    [TestCase("=")]
    [TestCase("<>")]
    public void Operators_HaveCorrectSql(string expected)
    {
        string actual = expected switch
        {
            "<"  => DbCheck.Operators.LessThan.Sql,
            ">"  => DbCheck.Operators.GreaterThan.Sql,
            "<=" => DbCheck.Operators.LessOrEqual.Sql,
            ">=" => DbCheck.Operators.GreaterOrEqual.Sql,
            "="  => DbCheck.Operators.Equal.Sql,
            "<>" => DbCheck.Operators.NotEqual.Sql,
            _    => throw new ArgumentException(expected)
        };
        Assert.That(actual, Is.EqualTo(expected));
    }

    // ------------------------------------------------------------------
    // DbCheck.Len
    // ------------------------------------------------------------------

    [Test]
    public void Len_LessThan_ProducesCorrectSql()
    {
        var expr = DbCheck.Len(DbCheck.Value("Email"), DbCheck.Operators.LessThan, 25);
        Assert.That(expr.Sql, Is.EqualTo("length(\"email\") < 25"));
    }

    [Test]
    public void Len_GreaterOrEqual_ProducesCorrectSql()
    {
        var expr = DbCheck.Len(DbCheck.Column("username"), DbCheck.Operators.GreaterOrEqual, 3);
        Assert.That(expr.Sql, Is.EqualTo("length(\"username\") >= 3"));
    }

    // ------------------------------------------------------------------
    // DbCheck.StartsWith / EndsWith / Contains
    // ------------------------------------------------------------------

    [Test]
    public void StartsWith_ProducesLikeWithPercentSuffix()
    {
        var expr = DbCheck.StartsWith(DbCheck.Column("phone"), "+420");
        Assert.That(expr.Sql, Is.EqualTo("\"phone\" LIKE '+420%'"));
    }

    [Test]
    public void StartsWith_EscapesSingleQuoteInPrefix()
    {
        var expr = DbCheck.StartsWith(DbCheck.Column("note"), "it's");
        Assert.That(expr.Sql, Is.EqualTo("\"note\" LIKE 'it''s%'"));
    }

    [Test]
    public void EndsWith_ProducesLikeWithPercentPrefix()
    {
        var expr = DbCheck.EndsWith(DbCheck.Column("email"), "@example.com");
        Assert.That(expr.Sql, Is.EqualTo("\"email\" LIKE '%@example.com'"));
    }

    [Test]
    public void Contains_ProducesLikeWithBothPercents()
    {
        var expr = DbCheck.Contains(DbCheck.Column("bio"), "admin");
        Assert.That(expr.Sql, Is.EqualTo("\"bio\" LIKE '%admin%'"));
    }

    // ------------------------------------------------------------------
    // DbCheck.Regex
    // ------------------------------------------------------------------

    [Test]
    public void Regex_ProducesPostgresRegexOperator()
    {
        var expr = DbCheck.Regex(DbCheck.Column("tag"), "^[a-z0-9_-]{3,16}$");
        Assert.That(expr.Sql, Is.EqualTo("\"tag\" ~ '^[a-z0-9_-]{3,16}$'"));
    }

    [Test]
    public void Regex_EscapesSingleQuoteInPattern()
    {
        var expr = DbCheck.Regex(DbCheck.Column("code"), "it's");
        Assert.That(expr.Sql, Is.EqualTo("\"code\" ~ 'it''s'"));
    }

    // ------------------------------------------------------------------
    // DbCheck.Not
    // ------------------------------------------------------------------

    [Test]
    public void Not_WrapsExpressionCorrectly()
    {
        var inner = DbCheck.StartsWith(DbCheck.Column("phone"), "+420");
        var expr = DbCheck.Not(inner);
        Assert.That(expr.Sql, Is.EqualTo("NOT (\"phone\" LIKE '+420%')"));
    }

    [Test]
    public void Not_CanBeNested()
    {
        var expr = DbCheck.Not(DbCheck.Not(DbCheck.Column("active")));
        Assert.That(expr.Sql, Is.EqualTo("NOT (NOT (\"active\"))"));
    }

    // ------------------------------------------------------------------
    // DbCheck.And
    // ------------------------------------------------------------------

    [Test]
    public void And_TwoExprs_WrapsWithAnd()
    {
        var a = DbCheck.Len(DbCheck.Column("email"), DbCheck.Operators.LessThan, 50);
        var b = DbCheck.Len(DbCheck.Column("email"), DbCheck.Operators.GreaterOrEqual, 5);
        var expr = DbCheck.And(a, b);
        Assert.That(expr.Sql, Is.EqualTo("(length(\"email\") < 50 AND length(\"email\") >= 5)"));
    }

    [Test]
    public void And_ThreeExprs_AllJoined()
    {
        var a = DbCheck.Column("a");
        var b = DbCheck.Column("b");
        var c = DbCheck.Column("c");
        var expr = DbCheck.And(a, b, c);
        Assert.That(expr.Sql, Is.EqualTo("(\"a\" AND \"b\" AND \"c\")"));
    }

    // ------------------------------------------------------------------
    // DbCheck.Or
    // ------------------------------------------------------------------

    [Test]
    public void Or_TwoExprs_WrapsWithOr()
    {
        var a = DbCheck.Column("admin");
        var b = DbCheck.Column("superuser");
        var expr = DbCheck.Or(a, b);
        Assert.That(expr.Sql, Is.EqualTo("(\"admin\" OR \"superuser\")"));
    }

    // ------------------------------------------------------------------
    // DbCheck.Eq
    // ------------------------------------------------------------------

    [Test]
    public void Eq_ProducesEqualityExpression()
    {
        var expr = DbCheck.Eq(DbCheck.Column("status"), DbCheck.Literal("active"));
        Assert.That(expr.Sql, Is.EqualTo("\"status\" = 'active'"));
    }

    // ------------------------------------------------------------------
    // DbCheck.Literal
    // ------------------------------------------------------------------

    [Test]
    public void Literal_String_QuotesAndEscapes()
    {
        var expr = DbCheck.Literal("it's fine");
        Assert.That(expr.Sql, Is.EqualTo("'it''s fine'"));
    }

    [Test]
    public void Literal_Long_NoQuotes()
    {
        var expr = DbCheck.Literal(42L);
        Assert.That(expr.Sql, Is.EqualTo("42"));
    }

    [Test]
    public void Literal_Double_InvariantCulture()
    {
        var expr = DbCheck.Literal(3.14);
        Assert.That(expr.Sql, Is.EqualTo("3.14"));
    }

    // ------------------------------------------------------------------
    // DbCheckExpr implicit string conversion
    // ------------------------------------------------------------------

    [Test]
    public void DbCheckExpr_ImplicitConversionToString_ReturnsSql()
    {
        DbCheckExpr expr = new DbCheckExpr("some sql");
        string s = expr;
        Assert.That(s, Is.EqualTo("some sql"));
    }

    [Test]
    public void DbCheckExpr_ToString_ReturnsSql()
    {
        var expr = DbCheck.Column("id");
        Assert.That(expr.ToString(), Is.EqualTo("\"id\""));
    }

    // ------------------------------------------------------------------
    // Composed expressions — mirrors the continue.md examples
    // ------------------------------------------------------------------

    [Test]
    public void ComposedExample_LenLessThan25()
    {
        // DbCheck.Len(DbCheck.Value(nameof(email)), DbCheck.Operators.LessThan, 25)
        var expr = DbCheck.Len(DbCheck.Value("Email"), DbCheck.Operators.LessThan, 25);
        Assert.That(expr.Sql, Is.EqualTo("length(\"email\") < 25"));
    }

    [Test]
    public void ComposedExample_NotStartsWith()
    {
        // DbCheck.Not(DbCheck.StartsWith(DbCheck.Value(nameof(PhoneNumber)), "+420"))
        var expr = DbCheck.Not(DbCheck.StartsWith(DbCheck.Value("PhoneNumber"), "+420"));
        Assert.That(expr.Sql, Is.EqualTo("NOT (\"phone_number\" LIKE '+420%')"));
    }

    [Test]
    public void ComposedExample_ClassLevelAndConstraint()
    {
        var expr = DbCheck.And(
            DbCheck.Len(DbCheck.Value("Email"), DbCheck.Operators.LessThan, 25),
            DbCheck.Not(DbCheck.StartsWith(DbCheck.Value("PhoneNumber"), "+420"))
        );
        Assert.That(expr.Sql, Is.EqualTo(
            "(length(\"email\") < 25 AND NOT (\"phone_number\" LIKE '+420%'))"));
    }

    [Test]
    public void ComposedExample_RegexOnTag()
    {
        // DbCheck.Regex(DbCheck.Column(col), "^[a-z0-9_-]{3,16}$") in Build(col)
        var expr = DbCheck.Regex(DbCheck.Column("tag"), "^[a-z0-9_-]{3,16}$");
        Assert.That(expr.Sql, Is.EqualTo("\"tag\" ~ '^[a-z0-9_-]{3,16}$'"));
    }

    // ------------------------------------------------------------------
    // IDbCheckExpression — class-level and property-level provider
    // ------------------------------------------------------------------

    private class UserLevelCheck : IDbCheckExpression
    {
        public DbCheckExpr Build(string? columnName) => DbCheck.And(
            DbCheck.Len(DbCheck.Value("Email"), DbCheck.Operators.LessThan, 25),
            DbCheck.Not(DbCheck.StartsWith(DbCheck.Value("PhoneNumber"), "+420"))
        );
    }

    private class TagRegexCheck : IDbCheckExpression
    {
        public DbCheckExpr Build(string? columnName) =>
            DbCheck.Regex(DbCheck.Column(columnName!), "^[a-z0-9_-]{3,16}$");
    }

    [Test]
    public void IDbCheckExpression_ClassLevel_BuildWithNullColumn()
    {
        var check = new UserLevelCheck();
        var expr = check.Build(null);
        Assert.That(expr.Sql, Is.EqualTo(
            "(length(\"email\") < 25 AND NOT (\"phone_number\" LIKE '+420%'))"));
    }

    [Test]
    public void IDbCheckExpression_PropertyLevel_BuildUsesColumnName()
    {
        var check = new TagRegexCheck();
        var expr = check.Build("tag");
        Assert.That(expr.Sql, Is.EqualTo("\"tag\" ~ '^[a-z0-9_-]{3,16}$'"));
    }

    [Test]
    public void IDbCheckExpression_PropertyLevel_DifferentColumnNames_ProduceDifferentSql()
    {
        var check = new TagRegexCheck();
        Assert.That(check.Build("username").Sql, Is.EqualTo("\"username\" ~ '^[a-z0-9_-]{3,16}$'"));
        Assert.That(check.Build("slug").Sql,     Is.EqualTo("\"slug\" ~ '^[a-z0-9_-]{3,16}$'"));
    }

    // ------------------------------------------------------------------
    // CheckAttribute constructors
    // ------------------------------------------------------------------

    [Test]
    public void CheckAttribute_StringCtor_SetsStatement()
    {
        var attr = new CheckAttribute("length(\"email\") < 25");
        Assert.That(attr.Statement, Is.EqualTo("length(\"email\") < 25"));
        Assert.That(attr.ExpressionType, Is.Null);
    }

    [Test]
    public void CheckAttribute_TypeCtor_SetsExpressionType()
    {
        var attr = new CheckAttribute(typeof(UserLevelCheck));
        Assert.That(attr.ExpressionType, Is.EqualTo(typeof(UserLevelCheck)));
        Assert.That(attr.Statement, Is.Null);
    }

    [Test]
    public void CheckAttribute_TypeCtor_TypeMustImplementIDbCheckExpression()
    {
        // Compile-time: any Type is accepted. Runtime: the AssemblyAnalyzer validates.
        // We verify that a correctly typed expression CAN be instantiated and called.
        var attr = new CheckAttribute(typeof(TagRegexCheck));
        var instance = (IDbCheckExpression)Activator.CreateInstance(attr.ExpressionType!)!;
        var sql = instance.Build("tag").Sql;
        Assert.That(sql, Is.EqualTo("\"tag\" ~ '^[a-z0-9_-]{3,16}$'"));
    }

    [Test]
    public void CheckAttribute_NameProperty_IsNullByDefault()
    {
        Assert.That(new CheckAttribute("x").Name, Is.Null);
        Assert.That(new CheckAttribute(typeof(UserLevelCheck)).Name, Is.Null);
    }

    [Test]
    public void CheckAttribute_NameProperty_CanBeSet()
    {
        var attr = new CheckAttribute("x") { Name = "CK_email_len" };
        Assert.That(attr.Name, Is.EqualTo("CK_email_len"));
    }
}
