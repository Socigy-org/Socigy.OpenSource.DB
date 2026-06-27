using System.Collections.Generic;
using System.Net;
using Socigy.OpenSource.DB.HashiCorp;
using VaultSharp.Core;

namespace Socigy.OpenSource.DB.HashiCorp.Tests;

/// <summary>
/// The envelope keyring is bootstrapped (a fresh DEK minted and WRITTEN over the secret path) only when the read
/// reports the secret genuinely does not exist. Treating any other failure as "first run" overwrote an existing
/// keyring with a new current=1 DEK, making every previously-encrypted row permanently undecryptable. Only a 404
/// may trigger bootstrap; every other status must propagate.
/// </summary>
[TestFixture]
public class VaultEnvelopeBootstrapTests
{
    [Test]
    public void NotFound_IsTreatedAsFirstRun()
    {
        Assert.That(VaultEnvelopeEncryptor.IsSecretNotFound(
            new VaultApiException(HttpStatusCode.NotFound, "no secret")), Is.True);
    }

    [TestCase(HttpStatusCode.ServiceUnavailable)]   // 503 — Vault sealed / standby
    [TestCase(HttpStatusCode.TooManyRequests)]      // 429 — rate limited
    [TestCase(HttpStatusCode.Forbidden)]            // 403 — read-denied policy (write may still succeed)
    [TestCase(HttpStatusCode.InternalServerError)]  // 500
    [TestCase(HttpStatusCode.BadGateway)]           // 502
    public void NonNotFound_MustNotBootstrap(HttpStatusCode status)
    {
        Assert.That(VaultEnvelopeEncryptor.IsSecretNotFound(
            new VaultApiException(status, "transient or permission error")), Is.False,
            $"a {(int)status} must propagate, never overwrite the existing keyring");
    }

    // Only a genuine 404 (null secret) may bootstrap-and-overwrite. A secret that EXISTS but whose keyring field is
    // missing/empty must NOT be treated as first-run — overwriting it would discard existing wrapped DEKs and make
    // encrypted rows undecryptable (the same data-loss the 404-only fix prevents, via a different door).
    [Test]
    public void ClassifyKeyringRead_NullSecret_IsFirstRun()
    {
        Assert.That(VaultEnvelopeEncryptor.ClassifyKeyringRead(null, "keyring", out _),
            Is.EqualTo(VaultEnvelopeEncryptor.KeyringReadState.FirstRun));
    }

    [Test]
    public void ClassifyKeyringRead_FieldPresent_IsPresent()
    {
        var secret = new Dictionary<string, object> { ["keyring"] = "v1:abc" };
        Assert.That(VaultEnvelopeEncryptor.ClassifyKeyringRead(secret, "keyring", out var raw),
            Is.EqualTo(VaultEnvelopeEncryptor.KeyringReadState.Present));
        Assert.That(raw, Is.EqualTo("v1:abc"));
    }

    [Test]
    public void ClassifyKeyringRead_SecretExistsButFieldMissing_IsExistsButFieldEmpty()
    {
        var secret = new Dictionary<string, object> { ["other_field"] = "x" };
        Assert.That(VaultEnvelopeEncryptor.ClassifyKeyringRead(secret, "keyring", out _),
            Is.EqualTo(VaultEnvelopeEncryptor.KeyringReadState.ExistsButFieldEmpty),
            "a secret that exists but lacks the keyring field must NOT be overwritten");
    }

    [Test]
    public void ClassifyKeyringRead_SecretExistsButFieldEmpty_IsExistsButFieldEmpty()
    {
        var secret = new Dictionary<string, object> { ["keyring"] = "" };
        Assert.That(VaultEnvelopeEncryptor.ClassifyKeyringRead(secret, "keyring", out _),
            Is.EqualTo(VaultEnvelopeEncryptor.KeyringReadState.ExistsButFieldEmpty));
    }
}
