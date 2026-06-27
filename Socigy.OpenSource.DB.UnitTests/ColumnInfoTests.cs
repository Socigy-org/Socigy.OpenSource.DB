using System;
using Socigy.OpenSource.DB.Core.CommandBuilders;

namespace Socigy.OpenSource.DB.UnitTests
{
    /// <summary>
    /// No-database tests for <see cref="ColumnInfo.ApplyDbValue{T}"/>, the shared read-side converter that the
    /// generated row-materialization, public <c>ReadValue</c>, and aggregate/scalar paths now route through.
    /// These lock the width-tolerant narrowing (unsigned, byte) and the timestamptz -> DateTimeOffset mapping.
    /// </summary>
    [TestFixture]
    public class ColumnInfoTests
    {
        private enum ByteEnum : byte { A = 1, B = 200 }
        private enum IntEnum { X = 0, Y = 70000 }

        // Unsigned columns are stored widened (ushort->int, uint->bigint, ulong->numeric); the DB hands the
        // widened type back and ApplyDbValue must narrow it (a raw cast would throw).
        [Test]
        public void Narrows_widened_unsigned_storage()
        {
            Assert.That(ColumnInfo.ApplyDbValue<ushort>((int)40000), Is.EqualTo((ushort)40000));
            Assert.That(ColumnInfo.ApplyDbValue<uint>((long)4000000000), Is.EqualTo(4000000000u));
            Assert.That(ColumnInfo.ApplyDbValue<ulong>((decimal)9000000000000000000), Is.EqualTo(9000000000000000000ul));
        }

        // byte/sbyte are stored as smallint, so the DB returns a short.
        [Test]
        public void Narrows_smallint_storage_to_byte()
        {
            Assert.That(ColumnInfo.ApplyDbValue<byte>((short)200), Is.EqualTo((byte)200));
            Assert.That(ColumnInfo.ApplyDbValue<sbyte>((short)-5), Is.EqualTo((sbyte)-5));
        }

        // Npgsql returns a timestamptz as a UTC DateTime; ApplyDbValue maps it onto a DateTimeOffset target
        // (Convert.ChangeType cannot, since DateTimeOffset is not IConvertible).
        [Test]
        public void Maps_utc_datetime_to_datetimeoffset()
        {
            var utc = new DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc);
            var result = ColumnInfo.ApplyDbValue<DateTimeOffset>(utc);
            Assert.That(result.Offset, Is.EqualTo(TimeSpan.Zero));
            Assert.That(result.UtcDateTime, Is.EqualTo(utc));
        }

        [Test]
        public void Maps_unspecified_datetime_to_datetimeoffset_as_utc()
        {
            var unspecified = new DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Unspecified);
            var result = ColumnInfo.ApplyDbValue<DateTimeOffset>(unspecified);
            Assert.That(result.Offset, Is.EqualTo(TimeSpan.Zero));
            Assert.That(result.UtcDateTime, Is.EqualTo(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc)));
        }

        // Enums read via their underlying integer regardless of the DB storage width.
        [Test]
        public void Reads_enum_from_widened_integer()
        {
            Assert.That(ColumnInfo.ApplyDbValue<ByteEnum>((short)200), Is.EqualTo(ByteEnum.B));
            Assert.That(ColumnInfo.ApplyDbValue<IntEnum>((long)70000), Is.EqualTo(IntEnum.Y));
        }

        [Test]
        public void Null_and_dbnull_return_default()
        {
            Assert.That(ColumnInfo.ApplyDbValue<int?>(null), Is.Null);
            Assert.That(ColumnInfo.ApplyDbValue<int?>(DBNull.Value), Is.Null);
            Assert.That(ColumnInfo.ApplyDbValue<uint>(DBNull.Value), Is.EqualTo(0u));
        }
    }
}
