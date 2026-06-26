using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Socigy.OpenSource.DB.Core.Encryption;
using Socigy.OpenSource.DB.HashiCorp;

namespace Socigy.OpenSource.DB.HashiCorp.Tests;

/// <summary>
/// End-to-end tests for the two Vault Transit encryption modes against a <b>live</b> server. Verified against
/// both HashiCorp Vault and OpenBao (their KV-v2 and Transit APIs are wire-compatible). Marked
/// <see cref="ExplicitAttribute"/> so they only run on demand. Requires a dev server with the Transit
/// engine and these keys (use the <c>vault</c> or <c>bao</c> CLI):
/// <code>
///   vault secrets enable transit
///   vault write -f transit/keys/socigy-db          # envelope (non-derived)
///   vault write transit/keys/socigy-eaas derived=true   # EaaS (context-bound)
/// </code>
/// Override the address/token via <c>VAULT_ADDR</c> / <c>VAULT_TOKEN</c> (defaults: dev server + "root").
/// </summary>
[TestFixture, Explicit("Requires a live Vault dev server with the Transit engine configured.")]
public class VaultEncryptionIntegrationTests
{
    private static string Addr => Environment.GetEnvironmentVariable("VAULT_ADDR") ?? "http://127.0.0.1:8200";
    private static string Token => Environment.GetEnvironmentVariable("VAULT_TOKEN") ?? "root";

    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("users:ssn");
    private static readonly byte[] Plain = Encoding.UTF8.GetBytes("123-45-6789");

    [Test]
    public async Task Envelope_mode_round_trips_rotates_and_keeps_old_rows_readable()
    {
        var options = new VaultEnvelopeEncryptionOptions
        {
            Address = Addr,
            Token = Token,
            TransitKeyName = "socigy-db",
            KvMountPoint = "secret",
            // Unique path per run so a re-run bootstraps a fresh keyring instead of reusing a stale one.
            KeyringSecretPath = "socigy/itest-keyring-" + Guid.NewGuid().ToString("N"),
        };
        var encryptor = new VaultEnvelopeEncryptor(new VaultClientProvider(options), options);
        await encryptor.RefreshAsync();

        byte[] cipherV1 = encryptor.Encrypt(Plain, Aad);
        Assert.That(encryptor.Decrypt(cipherV1, Aad), Is.EqualTo(Plain));
        Assert.That(KeyringFieldEncryptor.TryGetKeyId(cipherV1, out int id1), Is.True);
        Assert.That(id1, Is.EqualTo(1), "first write should use key version 1");

        // Rotate: mints a new DEK and advances current to version 2.
        await encryptor.RotateAsync();

        // The pre-rotation row must still decrypt (its DEK is retained in the keyring).
        Assert.That(encryptor.Decrypt(cipherV1, Aad), Is.EqualTo(Plain), "old row must stay readable after rotation");

        byte[] cipherV2 = encryptor.Encrypt(Plain, Aad);
        Assert.That(KeyringFieldEncryptor.TryGetKeyId(cipherV2, out int id2), Is.True);
        Assert.That(id2, Is.EqualTo(2), "new write should use the rotated key version 2");
        Assert.That(encryptor.Decrypt(cipherV2, Aad), Is.EqualTo(Plain));

        // Context binding: a different table:column must not decrypt.
        Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() =>
            Task.FromResult(encryptor.Decrypt(cipherV2, Encoding.UTF8.GetBytes("orders:note"))));
    }

    [Test]
    public async Task Eaas_mode_round_trips_and_rewraps_old_ciphertext_to_latest_version()
    {
        var options = new VaultTransitEncryptionOptions
        {
            Address = Addr,
            Token = Token,
            TransitKeyName = "socigy-eaas",
        };
        var encryptor = new VaultTransitFieldEncryptor(new VaultClientProvider(options), options);
        await encryptor.RefreshAsync();

        // Encrypt/Decrypt are sync-over-async; run off the test thread to avoid any sync-context surprises.
        byte[] cipher = await Task.Run(() => encryptor.Encrypt(Plain, Aad));
        string cipherText = Encoding.UTF8.GetString(cipher);
        Assert.That(cipherText, Does.StartWith("vault:v"), "EaaS ciphertext is a Transit vault:vN: string");
        Assert.That(await Task.Run(() => encryptor.Decrypt(cipher, Aad)), Is.EqualTo(Plain));
        Assert.That(encryptor.NeedsUpgrade(cipher), Is.False, "freshly written value is already at the latest version");

        // Rotate the Transit key operator-side, then re-prime so the encryptor learns the new latest version.
        await RotateTransitKeyAsync("socigy-eaas");
        await encryptor.RefreshAsync();

        Assert.That(encryptor.NeedsUpgrade(cipher), Is.True, "value is now one version behind");

        byte[] rewrapped = await encryptor.UpgradeToCurrentAsync(cipher, Aad);
        Assert.That(Encoding.UTF8.GetString(rewrapped), Is.Not.EqualTo(cipherText), "rewrap produces a new ciphertext");
        Assert.That(encryptor.NeedsUpgrade(rewrapped), Is.False, "rewrapped value is at the latest version");
        Assert.That(await Task.Run(() => encryptor.Decrypt(rewrapped, Aad)), Is.EqualTo(Plain), "rewrapped value still decrypts");

        // Context binding via Transit's derived-key context: wrong table:column must fail (Vault returns
        // "message authentication failed"). CatchAsync because the thrown VaultApiException is a derived type.
        Assert.CatchAsync(() => Task.Run(() => encryptor.Decrypt(rewrapped, Encoding.UTF8.GetBytes("orders:note"))));
    }

    private static async Task RotateTransitKeyAsync(string keyName)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Vault-Token", Token);
        var response = await http.PostAsync($"{Addr}/v1/transit/keys/{keyName}/rotate", null);
        response.EnsureSuccessStatusCode();
    }
}
