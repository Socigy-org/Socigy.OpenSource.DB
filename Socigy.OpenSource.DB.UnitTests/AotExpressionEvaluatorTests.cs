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
    /// The WHERE translator evaluates parameter-independent sub-expressions (method calls, indexers, arithmetic
    /// among captured values, conditionals) WITHOUT <c>Expression.Compile()</c> (which is <c>[RequiresDynamicCode]</c>
    /// and unusable under NativeAOT) — via the reflection interpreter in <c>ExpressionEvaluator</c>. These assert the
    /// bound parameter value (and its type) match what the compiled delegate produced, for each fallback shape.
    /// </summary>
    [TestFixture]
    public class AotExpressionEvaluatorTests
    {
        private sealed class Foo
        {
            public int Id { get; set; }
            public long Big { get; set; }
            public string Name { get; set; } = "";
            public decimal Amount { get; set; }
            public DateTime At { get; set; }
        }

        private sealed class Helper
        {
            public string Prefix = "ab";
            public string Get() => "computed";
            public int Compute(int n) => n * 10;
        }

        private static object? BoundValue(Expression<Func<Foo, bool>> predicate)
        {
            var command = new NpgsqlCommand();
            GetColumnName columns = name => name;
            var visitor = new PostgresqlWhereVisitor(predicate.Parameters[0], columns, command);
            visitor.Parse(predicate.Body);
            Assert.That(command.Parameters, Has.Count.EqualTo(1), "expected exactly one bound parameter");
            return ((NpgsqlParameter)command.Parameters[0]!).Value;
        }

        [Test]
        public void StaticMethodCall_IsEvaluated()
        {
            Assert.That(BoundValue(x => x.Id == int.Parse("42")), Is.EqualTo(42));
        }

        [Test]
        public void InstanceMethodCall_OnCapturedObject_IsEvaluated()
        {
            var helper = new Helper();
            Assert.That(BoundValue(x => x.Name == helper.Get()), Is.EqualTo("computed"));
            Assert.That(BoundValue(x => x.Id == helper.Compute(5)), Is.EqualTo(50));
        }

        [Test]
        public void ArrayIndex_IsEvaluated()
        {
            var nums = new[] { 10, 20, 30 };
            Assert.That(BoundValue(x => x.Id == nums[2]), Is.EqualTo(30));
        }

        [Test]
        public void ListIndexer_IsEvaluated()
        {
            var names = new List<string> { "first", "second" };
            Assert.That(BoundValue(x => x.Name == names[1]), Is.EqualTo("second"));
        }

        [Test]
        public void IntArithmeticAmongCaptures_StaysInt()
        {
            int a = 3, b = 4;
            object? v = BoundValue(x => x.Id == a + b);
            Assert.That(v, Is.EqualTo(7));
            Assert.That(v, Is.TypeOf<int>(), "int+int must bind as int, not double/long");
        }

        [Test]
        public void DecimalArithmeticAmongCaptures_StaysDecimal()
        {
            decimal p = 2.5m, q = 4m;
            object? v = BoundValue(x => x.Amount == p * q);
            Assert.That(v, Is.EqualTo(10m));
            Assert.That(v, Is.TypeOf<decimal>());
        }

        [Test]
        public void LongArithmeticAmongCaptures_StaysLong()
        {
            long a = 5_000_000_000L, b = 2L;
            object? v = BoundValue(x => x.Big == a + b);
            Assert.That(v, Is.EqualTo(5_000_000_002L));
            Assert.That(v, Is.TypeOf<long>());
        }

        [Test]
        public void Conditional_IsEvaluated()
        {
            bool flag = true;
            Assert.That(BoundValue(x => x.Id == (flag ? 1 : 2)), Is.EqualTo(1));
            flag = false;
            Assert.That(BoundValue(x => x.Id == (flag ? 1 : 2)), Is.EqualTo(2));
        }

        [Test]
        public void MethodCallChain_IsEvaluated()
        {
            var baseDate = new DateTime(2020, 1, 10, 0, 0, 0, DateTimeKind.Unspecified);
            object? v = BoundValue(x => x.At == baseDate.AddDays(5));
            Assert.That(v, Is.EqualTo(new DateTime(2020, 1, 15, 0, 0, 0, DateTimeKind.Unspecified)));
        }

        [Test]
        public void CapturedConvert_IsEvaluated()
        {
            short s = 7;
            // (int)s among captures -> Convert node; result binds as int 7.
            Assert.That(BoundValue(x => x.Id == (int)s), Is.EqualTo(7));
        }

        [Test]
        public void NegateInt_StaysInt()
        {
            int a = 5;
            object? v = BoundValue(x => x.Id == -a);
            Assert.That(v, Is.EqualTo(-5));
            Assert.That(v, Is.TypeOf<int>(), "negating an int must stay int, not become double");
        }

        [Test]
        public void NegateLargeLong_PreservesPrecision()
        {
            // 2^53 + 1 cannot be represented exactly as a double; routing negation through double would corrupt it.
            long a = (1L << 53) + 1;     // 9_007_199_254_740_993
            object? v = BoundValue(x => x.Big == -a);
            Assert.That(v, Is.EqualTo(-9_007_199_254_740_993L));
            Assert.That(v, Is.TypeOf<long>());
        }

        [Test]
        public void OnesComplementUnsigned_KeepsUnsignedValue()
        {
            // ~0u is uint.MaxValue (4294967295) in C#; evaluating it as ~(long)0 = -1 would bind the wrong value.
            uint u = 0;
            object? v = BoundValue(x => x.Big == ~u);
            Assert.That(v, Is.EqualTo(4294967295L), "~uint must keep the unsigned bit pattern, not sign-extend to -1");
        }

        [Test]
        public void CastDoubleToInt_TruncatesTowardZero()
        {
            // C# (int)3.7 == 3 (truncate); Convert.ChangeType would round to 4.
            double d = 3.7;
            Assert.That(BoundValue(x => x.Id == (int)d), Is.EqualTo(3));
            double n = -3.7;
            Assert.That(BoundValue(x => x.Id == (int)n), Is.EqualTo(-3));
        }

        [Test]
        public void LiftedNullableBoolLogic_MatchesCSharpThreeValuedLogic()
        {
            bool? n = null;
            // `true & null` is null in C# (must not NRE on (bool)null), `false & null` is false (false dominates).
            Expression<Func<bool?>> andNull = () => true & n;
            Expression<Func<bool?>> andFalse = () => false & n;
            Expression<Func<bool?>> orTrue = () => n | true;
            Expression<Func<bool?>> xorNull = () => n ^ true;
            Assert.That(Core.Parsers.ExpressionEvaluator.Evaluate(andNull.Body), Is.Null, "true & null == null");
            Assert.That(Core.Parsers.ExpressionEvaluator.Evaluate(andFalse.Body), Is.EqualTo(false), "false & null == false");
            Assert.That(Core.Parsers.ExpressionEvaluator.Evaluate(orTrue.Body), Is.EqualTo(true), "null | true == true");
            Assert.That(Core.Parsers.ExpressionEvaluator.Evaluate(xorNull.Body), Is.Null, "null ^ true == null");
        }

        [Test]
        public void LiftedNullableArithmetic_NullOperand_FoldsToNull()
        {
            // (int?)null + 5 is null in C#, not 0; the comparison must become IS NULL, not bind 0.
            int? a = null; int b = 5;
            var command = new NpgsqlCommand();
            GetColumnName columns = name => name;
            Expression<Func<Foo, bool>> pred = x => x.Id == a + b;
            var visitor = new PostgresqlWhereVisitor(pred.Parameters[0], columns, command);
            string sql = visitor.Parse(pred.Body);
            Assert.That(command.Parameters, Has.Count.EqualTo(0), "a null-folded right side must not bind 0");
            Assert.That(sql.ToUpperInvariant(), Does.Contain("IS NULL"));
        }
    }
}
