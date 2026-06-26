using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Socigy.OpenSource.DB.Core.Encryption;

namespace Socigy.OpenSource.DB.UnitTests
{
    /// <summary>
    /// No-database tests for the versioned-keyring envelope encryptor and the named-profile registry that back
    /// the Transit data-key envelope mode.
    /// </summary>
    [TestFixture]
    public class KeyringEncryptionUnitTests
    {
        private static byte[] NewKey()
        {
            var key = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(key);
            return key;
        }

        private static KeyringFieldEncryptor TwoKeyRing(int current, out byte[] k1, out byte[] k2)
        {
            k1 = NewKey();
            k2 = NewKey();
            return new KeyringFieldEncryptor(new Dictionary<int, byte[]> { { 1, k1 }, { 2, k2 } }, current);
        }

        [Test]
        public void Keyring_round_trips_and_embeds_current_id()
        {
            var enc = TwoKeyRing(current: 2, out _, out _);
            byte[] plain = Encoding.UTF8.GetBytes("super secret value");

            byte[] cipher = enc.Encrypt(plain);
            Assert.That(enc.Decrypt(cipher), Is.EqualTo(plain));
            Assert.That(KeyringFieldEncryptor.TryGetKeyId(cipher, out int id), Is.True);
            Assert.That(id, Is.EqualTo(2));
        }

        [Test]
        public void Keyring_decrypts_old_version_after_current_advances()
        {
            byte[] k1 = NewKey(), k2 = NewKey();
            var v1 = new KeyringFieldEncryptor(new Dictionary<int, byte[]> { { 1, k1 } }, 1);
            byte[] plain = Encoding.UTF8.GetBytes("written under v1");
            byte[] oldCipher = v1.Encrypt(plain);

            // Later the keyring gains key 2 and current moves to 2; the v1 row must still decrypt.
            var v2 = new KeyringFieldEncryptor(new Dictionary<int, byte[]> { { 1, k1 }, { 2, k2 } }, 2);
            Assert.That(v2.Decrypt(oldCipher), Is.EqualTo(plain));
            Assert.That(v2.Encrypt(plain), Is.Not.EqualTo(oldCipher)); // new writes use v2
        }

        [Test]
        public void Keyring_unknown_or_retired_version_throws()
        {
            byte[] k1 = NewKey(), k2 = NewKey();
            var v1 = new KeyringFieldEncryptor(new Dictionary<int, byte[]> { { 1, k1 } }, 1);
            byte[] cipher = v1.Encrypt(Encoding.UTF8.GetBytes("x"));

            var onlyV2 = new KeyringFieldEncryptor(new Dictionary<int, byte[]> { { 2, k2 } }, 2);
            Assert.Throws<CryptographicException>(() => onlyV2.Decrypt(cipher));
        }

        [Test]
        public void Keyring_tampered_key_id_fails_mac()
        {
            var enc = TwoKeyRing(current: 1, out _, out _);
            byte[] cipher = enc.Encrypt(Encoding.UTF8.GetBytes("bind me"));
            cipher[4] ^= 0x01; // flip a key-id byte -> selects another key / breaks the bound AAD
            Assert.Throws<CryptographicException>(() => enc.Decrypt(cipher));
        }

        [Test]
        public void Keyring_binds_associated_data_to_context()
        {
            var enc = TwoKeyRing(current: 2, out _, out _);
            byte[] plain = Encoding.UTF8.GetBytes("123-45-6789");
            byte[] usersSsn = Encoding.UTF8.GetBytes("users:ssn");
            byte[] ordersNote = Encoding.UTF8.GetBytes("orders:note");

            byte[] cipher = enc.Encrypt(plain, usersSsn);
            Assert.That(enc.Decrypt(cipher, usersSsn), Is.EqualTo(plain));
            Assert.Throws<CryptographicException>(() => enc.Decrypt(cipher, ordersNote));
            Assert.Throws<CryptographicException>(() => enc.Decrypt(cipher));
        }

        [Test]
        public void Keyring_requires_current_id_present()
        {
            Assert.Throws<ArgumentException>(() =>
                new KeyringFieldEncryptor(new Dictionary<int, byte[]> { { 1, NewKey() } }, currentId: 5));
        }

        // ── IReencryptableEncryptor ────────────────────────────────────────────────
        [Test]
        public void Keyring_needs_upgrade_only_for_non_current_versions()
        {
            byte[] k1 = NewKey(), k2 = NewKey();
            var v1 = new KeyringFieldEncryptor(new Dictionary<int, byte[]> { { 1, k1 } }, 1);
            byte[] oldCipher = v1.Encrypt(Encoding.UTF8.GetBytes("old"));

            var v2 = new KeyringFieldEncryptor(new Dictionary<int, byte[]> { { 1, k1 }, { 2, k2 } }, 2);
            Assert.That(v2.NeedsUpgrade(oldCipher), Is.True);
            Assert.That(v2.NeedsUpgrade(v2.Encrypt(Encoding.UTF8.GetBytes("new"))), Is.False);
        }

        [Test]
        public async Task Keyring_upgrade_to_current_rewrites_to_current_id_preserving_plaintext()
        {
            byte[] k1 = NewKey(), k2 = NewKey();
            byte[] aad = Encoding.UTF8.GetBytes("users:ssn");
            var v1 = new KeyringFieldEncryptor(new Dictionary<int, byte[]> { { 1, k1 } }, 1);
            byte[] plain = Encoding.UTF8.GetBytes("upgrade me");
            byte[] oldCipher = v1.Encrypt(plain, aad);

            var v2 = new KeyringFieldEncryptor(new Dictionary<int, byte[]> { { 1, k1 }, { 2, k2 } }, 2);
            byte[] upgraded = await v2.UpgradeToCurrentAsync(oldCipher, aad);

            KeyringFieldEncryptor.TryGetKeyId(upgraded, out int id);
            Assert.That(id, Is.EqualTo(2));
            Assert.That(v2.Decrypt(upgraded, aad), Is.EqualTo(plain));
        }

        // ── SocigyFieldEncryption profile registry ─────────────────────────────────
        [Test]
        public void Profile_registry_routes_to_named_and_default_encryptors()
        {
            var def = new AesFieldEncryptor(NewKey());
            var transit = new AesFieldEncryptor(NewKey());
            SocigyFieldEncryption.Configure(def);
            SocigyFieldEncryption.Configure("transit", transit);

            Assert.That(SocigyFieldEncryption.Require(), Is.SameAs(def));
            Assert.That(SocigyFieldEncryption.Require(null), Is.SameAs(def));
            Assert.That(SocigyFieldEncryption.Require(""), Is.SameAs(def));
            Assert.That(SocigyFieldEncryption.Require("transit"), Is.SameAs(transit));
        }

        [Test]
        public void Profile_registry_unknown_profile_throws_clear_error()
        {
            SocigyFieldEncryption.Configure(new AesFieldEncryptor(NewKey()));
            var ex = Assert.Throws<InvalidOperationException>(() => SocigyFieldEncryption.Require("does-not-exist"));
            Assert.That(ex!.Message, Does.Contain("does-not-exist"));
        }

        [Test]
        public void AddSocigyAesEncryption_configures_default_and_named_profiles()
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            byte[] key = NewKey();

            var returned = services.AddSocigyAesEncryption(key);                 // default profile
            services.AddSocigyAesEncryption(Convert.ToBase64String(NewKey()), "transit"); // base64 + named profile

            Assert.That(returned, Is.SameAs(services), "helper returns the collection for chaining");

            var def = SocigyFieldEncryption.Require();
            Assert.That(SocigyFieldEncryption.Require("transit"), Is.Not.SameAs(def), "the named profile is a distinct encryptor");

            byte[] plain = Encoding.UTF8.GetBytes("secret");
            Assert.That(def.Decrypt(def.Encrypt(plain)), Is.EqualTo(plain));
        }

        [Test]
        public void FieldCrypto_profile_overload_round_trips_through_named_encryptor()
        {
            var transit = new AesFieldEncryptor(NewKey());
            SocigyFieldEncryption.Configure(new AesFieldEncryptor(NewKey())); // default
            SocigyFieldEncryption.Configure("transit", transit);

            object? cipher = global::Socigy.OpenSource.DB.Core.Encryption.FieldCrypto.Encrypt(
                "secret", typeof(string), "users:ssn", "transit");
            object? plain = global::Socigy.OpenSource.DB.Core.Encryption.FieldCrypto.Decrypt(
                cipher, typeof(string), "users:ssn", "transit");
            Assert.That(plain, Is.EqualTo("secret"));

            // Decrypting with the default profile (different key) must fail — proves routing happened.
            Assert.Throws<CryptographicException>(() =>
                global::Socigy.OpenSource.DB.Core.Encryption.FieldCrypto.Decrypt(cipher, typeof(string), "users:ssn", null));
        }
    }
}
