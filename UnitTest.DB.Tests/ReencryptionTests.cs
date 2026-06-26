using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Socigy.OpenSource.DB.Core.Encryption;
using Socigy.OpenSource.DB.Core.Encryption.Reencryption;
using UnitTest.DB;
using UnitTest.DB.Socigy.Generated;

namespace UnitTest.DB.Tests;

/// <summary>
/// End-to-end coverage for the bulk <see cref="FieldReencryptor"/> against real PostgreSQL: rows written under
/// an old keyring version are rewritten to the current version, stay decryptable, and a re-run is a no-op.
/// Uses a <see cref="KeyringFieldEncryptor"/> so values carry an upgradeable key id.
/// </summary>
[TestFixture]
public class ReencryptionTests : BaseUnitTest
{
    private static readonly byte[] _k1 = MakeKey(0x11);
    private static readonly byte[] _k2 = MakeKey(0x22);
    private IFieldEncryptor? _previous;

    private static readonly string[] EncryptedColumns = { "ssn", "pin", "token", "issued_at", "note", "manual" };

    [OneTimeSetUp]
    public void CaptureAmbient() => _previous = SocigyFieldEncryption.Current;

    [OneTimeTearDown]
    public void RestoreAmbient()
    {
        if (_previous != null) SocigyFieldEncryption.Configure(_previous);
    }

    private static byte[] MakeKey(byte seed)
    {
        var key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 7 + seed);
        return key;
    }

    private static KeyringFieldEncryptor Keyring(int current)
        => current == 1
            ? new KeyringFieldEncryptor(new Dictionary<int, byte[]> { { 1, _k1 } }, 1)
            : new KeyringFieldEncryptor(new Dictionary<int, byte[]> { { 1, _k1 }, { 2, _k2 } }, 2);

    private async Task<int> RawKeyIdAsync(Guid id, string column)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = $@"SELECT ""{column}"" FROM ""test_secrets"" WHERE ""id"" = @id";
        var p = cmd.CreateParameter();
        p.ParameterName = "@id";
        p.Value = id;
        cmd.Parameters.Add(p);

        var bytes = (byte[])(await cmd.ExecuteScalarAsync())!;
        KeyringFieldEncryptor.TryGetKeyId(bytes, out int keyId);
        return keyId;
    }

    private async Task<List<TestSecret>> SeedUnderV1Async(string owner, int count)
    {
        SocigyFieldEncryption.Configure(Keyring(1));
        var rows = new List<TestSecret>();
        for (int i = 0; i < count; i++)
            rows.Add(new TestSecret
            {
                Id = Guid.NewGuid(),
                Owner = owner,
                Ssn = $"ssn-{i}",
                Pin = 1000 + i,
                Token = Guid.NewGuid(),
                IssuedAt = new DateTime(2026, 1, 1 + i, 12, 0, 0, DateTimeKind.Utc),
                Note = $"note-{i}",
                Manual = $"man-{i}",
            });
        await TestSecret.InsertMultipleAsync(rows, Connection);
        return rows;
    }

    [Test]
    public async Task Reencrypts_rows_to_current_key_keeps_them_readable_and_is_idempotent()
    {
        await ClearAsync("test_secrets");
        var owner = $"reenc-{Guid.NewGuid():N}";
        var seeded = await SeedUnderV1Async(owner, 3);

        // Sanity: written under key version 1.
        Assert.That(await RawKeyIdAsync(seeded[0].Id, "ssn"), Is.EqualTo(1));

        // Advance the keyring so version 2 is current.
        SocigyFieldEncryption.Configure(Keyring(2));

        // DryRun reports what would change but writes nothing.
        var dry = await new FieldReencryptor().Add<TestSecret>().RunAsync(Connection, new ReencryptOptions { DryRun = true });
        Assert.That(dry.TotalCellsUpgraded, Is.EqualTo(3 * EncryptedColumns.Length));
        Assert.That(await RawKeyIdAsync(seeded[0].Id, "ssn"), Is.EqualTo(1), "DryRun must not write");

        // Real pass upgrades every encrypted cell to version 2.
        var report = await new FieldReencryptor().Add<TestSecret>().RunAsync(Connection);
        Assert.That(report.TotalRowsScanned, Is.EqualTo(3));
        Assert.That(report.TotalCellsUpgraded, Is.EqualTo(3 * EncryptedColumns.Length));
        foreach (var col in EncryptedColumns)
            Assert.That(await RawKeyIdAsync(seeded[0].Id, col), Is.EqualTo(2), $"{col} should be upgraded to v2");

        // Values still decrypt under the current keyring.
        var rows = await TestSecret.Query(x => x.Owner == owner).WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(3));
        var r0 = rows.Single(r => r.Ssn == "ssn-0");
        Assert.That(r0.Pin, Is.EqualTo(1000));
        Assert.That(r0.Note, Is.EqualTo("note-0"));
        Assert.That(r0.ManualDecrypted, Is.EqualTo("man-0"));

        // Re-running is a no-op (skip-if-current).
        var rerun = await new FieldReencryptor().Add<TestSecret>().RunAsync(Connection);
        Assert.That(rerun.TotalCellsUpgraded, Is.EqualTo(0));
    }

    [Test]
    public async Task AddDynamic_upgrades_a_runtime_named_table()
    {
        await ClearAsync("test_secrets");
        var owner = $"reenc-dyn-{Guid.NewGuid():N}";
        var seeded = await SeedUnderV1Async(owner, 1);

        SocigyFieldEncryption.Configure(Keyring(2));

        // AddDynamic binds the runtime table name for the SQL target while using the type's declared name for
        // the encryption context. For TestSecret the two coincide, exercising the dynamic entry point.
        var report = await new FieldReencryptor().AddDynamic<TestSecret>("test_secrets").RunAsync(Connection);

        Assert.That(report.TotalCellsUpgraded, Is.EqualTo(EncryptedColumns.Length));
        Assert.That(await RawKeyIdAsync(seeded[0].Id, "ssn"), Is.EqualTo(2));

        var row = await TestSecret.Query(x => x.Owner == owner).WithConnection(Connection).ExecuteAsync().FirstOrDefaultAsync();
        Assert.That(row, Is.Not.Null);
        Assert.That(row!.Ssn, Is.EqualTo("ssn-0"));
    }
}
