using System;
using Socigy.OpenSource.DB.Core.Parsers;

namespace Socigy.OpenSource.DB.UnitTests
{
    /// <summary>
    /// No-database tests for WHERE-clause parameter normalization. A predicate value must be normalized the same
    /// way the write paths are, or a filter compares against the wrong stored value (or throws): a Kind=Utc
    /// DateTime against a naive 'timestamp' would be inferred as 'timestamptz' and shifted by the session
    /// TimeZone (wrong rows); a non-UTC DateTimeOffset throws; unsigned CLR types have no Npgsql wire mapping.
    /// </summary>
    [TestFixture]
    public class WhereParameterTests
    {
        private enum Color { Red = 1, Green = 2 }

        [Test]
        public void Utc_DateTime_is_relabeled_unspecified()
        {
            var utc = new DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc);
            var result = WhereParameter.Normalize(utc);
            Assert.That(result, Is.InstanceOf<DateTime>());
            Assert.That(((DateTime)result!).Kind, Is.EqualTo(DateTimeKind.Unspecified));
            Assert.That((DateTime)result!, Is.EqualTo(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified)));
        }

        [Test]
        public void NonUtc_DateTimeOffset_is_normalized_to_utc()
        {
            var dto = new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.FromHours(2));
            var result = WhereParameter.Normalize(dto);
            Assert.That(result, Is.InstanceOf<DateTimeOffset>());
            Assert.That(((DateTimeOffset)result!).Offset, Is.EqualTo(TimeSpan.Zero));
            Assert.That((DateTimeOffset)result!, Is.EqualTo(dto.ToUniversalTime()));
        }

        [Test]
        public void Unsigned_types_are_widened()
        {
            Assert.That(WhereParameter.Normalize((ushort)40000), Is.EqualTo(40000).And.TypeOf<int>());
            Assert.That(WhereParameter.Normalize((uint)4000000000), Is.EqualTo(4000000000L).And.TypeOf<long>());
            Assert.That(WhereParameter.Normalize((ulong)9000000000000000000), Is.EqualTo(9000000000000000000m).And.TypeOf<decimal>());
        }

        [Test]
        public void Enum_is_bound_as_underlying_integer()
            => Assert.That(WhereParameter.Normalize(Color.Green), Is.EqualTo(2).And.TypeOf<int>());

        [Test]
        public void Already_normal_values_pass_through()
        {
            Assert.That(WhereParameter.Normalize("x"), Is.EqualTo("x"));
            Assert.That(WhereParameter.Normalize(5), Is.EqualTo(5));
            var utcZero = new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);
            Assert.That(WhereParameter.Normalize(utcZero), Is.EqualTo(utcZero));
        }
    }
}
