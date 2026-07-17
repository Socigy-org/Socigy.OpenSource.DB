using System;
using System.Security.Cryptography;
using Socigy.OpenSource.DB.Core.Encryption;

namespace Socigy.OpenSource.DB.UnitTests
{
    /// <summary>
    /// No-database tests for field-level encryption: the value &lt;-&gt; byte[] codec, the built-in
    /// AES-256-CBC + HMAC encryptor, and the ambient <see cref="SocigyFieldEncryption"/> holder.
    /// </summary>
    [TestFixture]
    public class EncryptionUnitTests
    {
        private static byte[] NewKey()
        {
            var key = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(key);
            return key;
        }

        // ── FieldValueCodec round-trips ─────────────────────────────────────────────
        private enum Color { Red = 1, Green = 2, Blue = 7 }

        [Test]
        public void Codec_round_trips_every_supported_type()
        {
            AssertRoundTrip(true, typeof(bool));
            AssertRoundTrip((byte)200, typeof(byte));
            AssertRoundTrip((sbyte)-5, typeof(sbyte));
            AssertRoundTrip((short)-1234, typeof(short));
            AssertRoundTrip((ushort)60000, typeof(ushort));
            AssertRoundTrip(123456, typeof(int));
            AssertRoundTrip(4000000000u, typeof(uint));
            AssertRoundTrip(-9876543210L, typeof(long));
            AssertRoundTrip(18000000000000000000UL, typeof(ulong));
            AssertRoundTrip(3.14f, typeof(float));
            AssertRoundTrip(2.718281828, typeof(double));
            AssertRoundTrip(1234.5678m, typeof(decimal));
            AssertRoundTrip('Z', typeof(char));
            AssertRoundTrip("héllo, wörld 🌍", typeof(string));
            AssertRoundTrip(Guid.NewGuid(), typeof(Guid));
            AssertRoundTrip(new DateTime(2026, 6, 5, 12, 30, 0, DateTimeKind.Utc), typeof(DateTime));
            AssertRoundTrip(new DateTimeOffset(2026, 6, 5, 12, 30, 0, TimeSpan.FromHours(2)), typeof(DateTimeOffset));
            AssertRoundTrip(TimeSpan.FromMinutes(90.5), typeof(TimeSpan));
            AssertRoundTrip(new byte[] { 1, 2, 3, 254, 255 }, typeof(byte[]));
            AssertRoundTrip(Color.Blue, typeof(Color));
        }

        [Test]
        public void Codec_round_trips_nullable_underlying_value()
        {
            // Callers pass the non-null underlying value with the declared (nullable) type.
            byte[] bytes = FieldValueCodec.Encode(42, typeof(int?));
            object decoded = FieldValueCodec.Decode(bytes, typeof(int?));
            Assert.That(decoded, Is.EqualTo(42));
        }

        [Test]
        public void Codec_throws_for_unsupported_type()
        {
            Assert.Throws<NotSupportedException>(() => FieldValueCodec.Encode(new object(), typeof(object)));
        }

        [Test]
        public void Codec_encodes_numbers_little_endian_for_portable_format()
        {
            // The on-disk format must be fixed (little-endian), not host-dependent, so ciphertext is portable.
            Assert.That(FieldValueCodec.Encode(0x01020304, typeof(int)),
                Is.EqualTo(new byte[] { 0x04, 0x03, 0x02, 0x01 }));
            Assert.That(FieldValueCodec.Encode((long)0x0102030405060708, typeof(long)),
                Is.EqualTo(new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 }));
        }

        private static void AssertRoundTrip(object value, Type type)
        {
            byte[] bytes = FieldValueCodec.Encode(value, type);
            object decoded = FieldValueCodec.Decode(bytes, type);
            Assert.That(decoded, Is.EqualTo(value), $"round-trip failed for {type}");
        }

        // ── AesFieldEncryptor ───────────────────────────────────────────────────────
        [Test]
        public void Aes_encrypt_decrypt_round_trips()
        {
            var enc = new AesFieldEncryptor(NewKey());
            byte[] plain = System.Text.Encoding.UTF8.GetBytes("super secret value");
            byte[] cipher = enc.Encrypt(plain);

            Assert.That(cipher, Is.Not.EqualTo(plain));
            Assert.That(enc.Decrypt(cipher), Is.EqualTo(plain));
        }

        [Test]
        public void Aes_uses_random_iv_so_ciphertexts_differ()
        {
            var enc = new AesFieldEncryptor(NewKey());
            byte[] plain = System.Text.Encoding.UTF8.GetBytes("same input");
            Assert.That(enc.Encrypt(plain), Is.Not.EqualTo(enc.Encrypt(plain)));
        }

        [Test]
        public void Aes_tampered_ciphertext_fails_integrity_check()
        {
            var enc = new AesFieldEncryptor(NewKey());
            byte[] cipher = enc.Encrypt(new byte[] { 1, 2, 3, 4 });
            cipher[cipher.Length - 1] ^= 0xFF; // flip a MAC byte
            Assert.Throws<CryptographicException>(() => enc.Decrypt(cipher));
        }

        [Test]
        public void Aes_wrong_key_cannot_decrypt()
        {
            var a = new AesFieldEncryptor(NewKey());
            var b = new AesFieldEncryptor(NewKey());
            byte[] cipher = a.Encrypt(new byte[] { 9, 8, 7 });
            Assert.Throws<CryptographicException>(() => b.Decrypt(cipher));
        }

        [Test]
        public void Aes_rejects_short_key()
        {
            Assert.Throws<ArgumentException>(() => new AesFieldEncryptor(new byte[8]));
        }

        [Test]
        public void Aes_associated_data_binds_ciphertext_to_its_context()
        {
            var enc = new AesFieldEncryptor(NewKey());
            byte[] plain = System.Text.Encoding.UTF8.GetBytes("123-45-6789");
            byte[] aadUsersSsn = System.Text.Encoding.UTF8.GetBytes("users:ssn");
            byte[] aadOrdersNote = System.Text.Encoding.UTF8.GetBytes("orders:note");

            byte[] cipher = enc.Encrypt(plain, aadUsersSsn);

            // Same context decrypts; a different column/table (relocation) or missing context fails.
            Assert.That(enc.Decrypt(cipher, aadUsersSsn), Is.EqualTo(plain));
            Assert.Throws<CryptographicException>(() => enc.Decrypt(cipher, aadOrdersNote));
            Assert.Throws<CryptographicException>(() => enc.Decrypt(cipher));
        }

        [Test]
        public void Aes_without_associated_data_still_round_trips()
        {
            var enc = new AesFieldEncryptor(NewKey());
            byte[] plain = System.Text.Encoding.UTF8.GetBytes("no aad");
            Assert.That(enc.Decrypt(enc.Encrypt(plain)), Is.EqualTo(plain));
        }

        [Test]
        public void Disposed_encryptor_zeroes_keys_and_refuses_use()
        {
            var enc = new AesFieldEncryptor(NewKey());
            byte[] cipher = enc.Encrypt(new byte[] { 1, 2, 3 });

            enc.Dispose();

            Assert.Throws<ObjectDisposedException>(() => enc.Encrypt(new byte[] { 1 }));
            Assert.Throws<ObjectDisposedException>(() => enc.Decrypt(cipher));
            Assert.DoesNotThrow(() => enc.Dispose()); // idempotent
        }

        // ── End-to-end via FieldCrypto + ambient holder ─────────────────────────────
        [Test]
        public void FieldCrypto_encrypts_and_decrypts_through_ambient_encryptor()
        {
            SocigyFieldEncryption.Configure(new AesFieldEncryptor(NewKey()));
            try
            {
                var original = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                object? cipher = FieldCrypto.Encrypt(original, typeof(DateTime));
                Assert.That(cipher, Is.InstanceOf<byte[]>());

                object? back = FieldCrypto.Decrypt(cipher, typeof(DateTime));
                Assert.That(back, Is.EqualTo(original));

                Assert.That(FieldCrypto.Encrypt(null, typeof(DateTime)), Is.Null);
                Assert.That(FieldCrypto.Decrypt(null, typeof(DateTime)), Is.Null);
                Assert.That(FieldCrypto.Decrypt(DBNull.Value, typeof(DateTime)), Is.Null);
            }
            finally
            {
                ResetAmbient();
            }
        }

        [Test]
        public void Require_throws_a_clear_error_when_unconfigured()
        {
            ResetAmbient();
            var ex = Assert.Throws<InvalidOperationException>(() => SocigyFieldEncryption.Require());
            Assert.That(ex!.Message, Does.Contain("[Encrypted]"));
        }

        // ── IsProfileConfigured: a readiness check that covers named profiles ────────
        // IsConfigured only reports the DEFAULT, so it cannot tell you whether an [Encrypted(Profile = "…")]
        // column is ready — the profile stays silently missing until the first write to such a column throws.
        [Test]
        public void IsProfileConfigured_tracks_named_profiles_independently_of_the_default()
        {
            // Unique per run: SocigyFieldEncryption is process-wide static and profiles are never removed.
            string profile = "unit-" + Guid.NewGuid().ToString("N");
            string never = "never-" + Guid.NewGuid().ToString("N");

            Assert.That(SocigyFieldEncryption.IsProfileConfigured(profile), Is.False, "not registered yet");

            SocigyFieldEncryption.Configure(profile, new AesFieldEncryptor(NewKey()));
            Assert.That(SocigyFieldEncryption.IsProfileConfigured(profile), Is.True);

            // The actual bug this closes: a configured default must not make an unregistered profile look ready.
            SocigyFieldEncryption.Configure(new AesFieldEncryptor(NewKey()));
            try
            {
                Assert.That(SocigyFieldEncryption.IsConfigured, Is.True, "default is configured");
                Assert.That(SocigyFieldEncryption.IsProfileConfigured(never), Is.False,
                    "a never-registered profile must report false even when the default is configured");

                // A null/empty profile means "the default".
                Assert.That(SocigyFieldEncryption.IsProfileConfigured(null), Is.True);
                Assert.That(SocigyFieldEncryption.IsProfileConfigured(""), Is.True);
            }
            finally
            {
                ResetAmbient();
            }

            Assert.That(SocigyFieldEncryption.IsProfileConfigured(null), Is.False, "default cleared");
            Assert.That(SocigyFieldEncryption.IsProfileConfigured(profile), Is.True, "named profiles are unaffected");
        }

        // ── AutoDecrypt = false (raw + lazy decrypt) ────────────────────────────────
        [Test]
        public void AutoDecrypt_false_exposes_raw_and_lazily_decrypts_into_field()
        {
            SocigyFieldEncryption.Configure(new AesFieldEncryptor(NewKey()));
            try
            {
                var secret = new UnitTest.DB.TestSecret();

                // Simulate materialization: the raw ciphertext lands in ManualRawEncrypted (private setter).
                // The generated decrypt getter binds the value to its "table:column" context, so encrypt with
                // the same context here.
                string ctx = UnitTest.DB.TestSecret.TableName + ":" + UnitTest.DB.TestSecret.ManualColumnName;
                byte[] cipher = (byte[])FieldCrypto.Encrypt("hush", typeof(string), ctx)!;
                typeof(UnitTest.DB.TestSecret).GetProperty("ManualRawEncrypted")!.SetValue(secret, cipher);

                Assert.That(secret.Manual, Is.EqualTo(""));               // true field not decrypted yet
                Assert.That(secret.ManualRawEncrypted, Is.EqualTo(cipher));
                Assert.That(secret.ManualDecrypted, Is.EqualTo("hush"));  // lazily decrypts...
                Assert.That(secret.Manual, Is.EqualTo("hush"));           // ...and caches into the field
            }
            finally { ResetAmbient(); }
        }

        // ── Query rejection (no DB) ─────────────────────────────────────────────────
        [Test]
        public void Encrypted_column_is_rejected_in_column_resolution()
        {
            // Non-encrypted columns resolve normally; encrypted ones throw a clear error (used by the
            // WHERE / ORDER BY / SELECT visitors, so predicates on encrypted columns fail fast).
            Assert.That(UnitTest.DB.TestSecret.GetColumnDbName("Owner"), Is.EqualTo("owner"));

            var ex = Assert.Throws<NotSupportedException>(() => UnitTest.DB.TestSecret.GetColumnDbName("Ssn"));
            Assert.That(ex!.Message, Does.Contain("Ssn").And.Contain("Encrypted"));
        }

        // Reset the ambient encryptor between tests via reflection (test-only).
        private static void ResetAmbient()
        {
            var field = typeof(SocigyFieldEncryption).GetField("_current",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            field!.SetValue(null, null);
        }
    }
}
