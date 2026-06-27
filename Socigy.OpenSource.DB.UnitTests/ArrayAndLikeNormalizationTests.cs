using System;
using System.Collections.Generic;
using Socigy.OpenSource.DB.Core.Parsers;

namespace Socigy.OpenSource.DB.UnitTests
{
    /// <summary>
    /// No-database tests for the collection <c>= ANY(@array)</c> element normalization and the LIKE null-pattern
    /// guard on the shared (translation + cache-replay) binding path.
    /// </summary>
    [TestFixture]
    public class ArrayAndLikeNormalizationTests
    {
        private enum Role { Reader = 1, Admin = 5 }

        // An array bound for `= ANY(@p)` must normalize each element exactly like the scalar `= @p` path, and the
        // array element type must match the normalized element (else Npgsql can't bind an enum[]/unsigned[] or
        // shifts/throws on DateTime[]/DateTimeOffset[]).
        [Test]
        public void Enum_array_is_widened_to_underlying_integer_array()
        {
            var result = ExpressionEvaluator.ToTypedArray(new[] { Role.Reader, Role.Admin }, typeof(Role));
            Assert.That(result, Is.InstanceOf<int[]>());
            Assert.That((int[])result!, Is.EqualTo(new[] { 1, 5 }));
        }

        [Test]
        public void Unsigned_arrays_are_widened()
        {
            Assert.That(ExpressionEvaluator.ToTypedArray(new List<uint> { 1, 4000000000 }, typeof(uint)), Is.InstanceOf<long[]>());
            Assert.That(ExpressionEvaluator.ToTypedArray(new ushort[] { 1, 40000 }, typeof(ushort)), Is.InstanceOf<int[]>());
            Assert.That(ExpressionEvaluator.ToTypedArray(new ulong[] { 1 }, typeof(ulong)), Is.InstanceOf<decimal[]>());
        }

        [Test]
        public void Utc_DateTime_array_elements_are_relabeled_unspecified()
        {
            var utc = new DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc);
            var result = (DateTime[])ExpressionEvaluator.ToTypedArray(new[] { utc }, typeof(DateTime))!;
            Assert.That(result[0].Kind, Is.EqualTo(DateTimeKind.Unspecified));
        }

        [Test]
        public void NonUtc_DateTimeOffset_array_elements_are_normalized_to_utc()
        {
            var dto = new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.FromHours(2));
            var result = (DateTimeOffset[])ExpressionEvaluator.ToTypedArray(new[] { dto }, typeof(DateTimeOffset))!;
            Assert.That(result[0].Offset, Is.EqualTo(TimeSpan.Zero));
            Assert.That(result[0], Is.EqualTo(dto.ToUniversalTime()));
        }

        [Test]
        public void Guid_array_passes_through_unchanged()
        {
            var g = Guid.NewGuid();
            Assert.That((Guid[])ExpressionEvaluator.ToTypedArray(new[] { g }, typeof(Guid))!, Is.EqualTo(new[] { g }));
        }

        // Regression: a Nullable<enum> / Nullable<unsigned> element type (e.g. `List<Role?>.Contains(x.NullableRole)`)
        // was not unwrapped, so the array stayed Role?[] while Normalize produced underlying ints -> CopyTo threw
        // InvalidCastException. The element type is now unwrapped, normalized, and re-wrapped as Nullable<normalized>
        // so a null element is still representable.
        [Test]
        public void Nullable_enum_array_widens_to_nullable_underlying()
        {
            var result = ExpressionEvaluator.ToTypedArray(new List<Role?> { Role.Admin, null }, typeof(Role?));
            Assert.That(result, Is.InstanceOf<int?[]>());
            Assert.That((int?[])result!, Is.EqualTo(new int?[] { 5, null }));
        }

        [Test]
        public void Nullable_unsigned_array_widens_to_nullable_widened()
        {
            var result = ExpressionEvaluator.ToTypedArray(new List<uint?> { 7u, null }, typeof(uint?));
            Assert.That(result, Is.InstanceOf<long?[]>());
            Assert.That((long?[])result!, Is.EqualTo(new long?[] { 7L, null }));
        }

        [Test]
        public void Nullable_int_array_with_null_still_works()
        {
            var result = ExpressionEvaluator.ToTypedArray(new List<int?> { 3, null }, typeof(int?));
            Assert.That(result, Is.InstanceOf<int?[]>());
            Assert.That((int?[])result!, Is.EqualTo(new int?[] { 3, null }));
        }

        // The LIKE null guard must fire on the shared Apply path (used by the cache-replay), not only during first
        // translation — otherwise a cached StartsWith/Contains shape replayed with a null value matches every row.
        [Test]
        public void Null_like_pattern_throws_on_the_shared_bind_path()
        {
            Assert.Throws<ArgumentNullException>(() => WhereParameter.Apply(ParamTransform.LikeStartsWith, null, null));
            Assert.Throws<ArgumentNullException>(() => WhereParameter.Apply(ParamTransform.LikeContains, null, null));
            Assert.Throws<ArgumentNullException>(() => WhereParameter.Apply(ParamTransform.LikeEndsWith, null, null));
        }

        [Test]
        public void NonNull_like_pattern_still_wraps_wildcards()
        {
            Assert.That(WhereParameter.Apply(ParamTransform.LikeContains, "abc", null), Is.EqualTo("%abc%"));
            Assert.That(WhereParameter.Apply(ParamTransform.LikeStartsWith, "abc", null), Is.EqualTo("abc%"));
            Assert.That(WhereParameter.Apply(ParamTransform.LikeEndsWith, "abc", null), Is.EqualTo("%abc"));
        }

        // The char-comparison rebind (char promoted to int -> bound as a 1-char string) must live on the shared
        // Apply path, so the cache-REPLAY of a char predicate reproduces the string instead of binding the raw int
        // code point (which would hit `character(1) = integer`). Both first-translation and replay call Apply here.
        [Test]
        public void CharString_transform_rebinds_code_point_as_string()
        {
            Assert.That(WhereParameter.Apply(ParamTransform.CharString, 65, null), Is.EqualTo("A"));
            Assert.That(WhereParameter.Apply(ParamTransform.CharString, (int)'Z', null), Is.EqualTo("Z"));
            Assert.That(WhereParameter.Apply(ParamTransform.CharString, null, null), Is.Null);
        }
    }
}
