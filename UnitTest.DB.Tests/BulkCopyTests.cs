using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using Socigy.OpenSource.DB.Core.Encryption;
using UnitTest.DB;
using UnitTest.DB.Socigy.Generated;
using Bulk = Socigy.OpenSource.DB.Core.Bulk;

namespace UnitTest.DB.Tests;

/// <summary>
/// End-to-end coverage for the binary COPY path (<see cref="Socigy.OpenSource.DB.Core.Bulk.BulkCopy"/>).
/// Exercises the Core bridge + the generator-emitted Npgsql COPY handler registered at module load, including
/// the JSON-column and NULL paths that COPY must wire-encode identically to the parameterized insert.
/// </summary>
[TestFixture]
public class BulkCopyTests : BaseUnitTest
{
    [OneTimeSetUp]
    public void ConfigureEncryption()
    {
        // Ambient encryptor for [Encrypted] columns (deterministic 32-byte test key). Process-wide and
        // idempotent; no other DB test exercises encryption, so configuring it here is safe.
        var key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 7 + 1);
        SocigyFieldEncryption.Configure(new AesFieldEncryptor(key));
    }

    [Test]
    public void Bridge_IsRegistered_WhenModelAssemblyLoaded()
    {
        // The generated __SocigyBulkCopyBridge registers the Npgsql handler via [ModuleInitializer], which the
        // runtime fires lazily on first use of the model assembly. Any real COPY call touches a model type and
        // triggers it; force it deterministically here so the test doesn't depend on execution order.
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(TestItem).Module.ModuleHandle);
        Assert.That(Bulk.BulkCopySupport.IsAvailable, Is.True);
    }

    [Test]
    public async Task BulkCopy_InsertsAllRows_AndRoundTrips()
    {
        var name = $"copy-{Guid.NewGuid():N}";
        var rows = new List<TestItem>();
        for (int i = 0; i < 50; i++)
            rows.Add(new TestItem { Id = Guid.NewGuid(), Name = name, Priority = i });

        ulong written = await Bulk.BulkCopy.InsertMultipleCopyAsync(rows, Connection);
        Assert.That(written, Is.EqualTo(50UL));

        // Read back through existing procedures to confirm the rows actually landed.
        Assert.That(await Procedures.CountItemsByName(Connection, name), Is.EqualTo(50));
        Assert.That(await Procedures.MaxPriorityByName(Connection, name), Is.EqualTo(49));
    }

    [Test]
    public async Task BulkCopy_EmptyInput_WritesNothing()
    {
        ulong written = await Bulk.BulkCopy.InsertMultipleCopyAsync(new List<TestItem>(), Connection);
        Assert.That(written, Is.EqualTo(0UL));
    }

    [Test]
    public async Task BulkCopy_JsonColumns_RoundTrip()
    {
        var id = Guid.NewGuid();
        var item = new TestJsonItem
        {
            Id = id,
            Name = "copy-json",
            RawData = """{"k":"v","n":7}""",
            Payload = new TestJsonPayload { Title = "title", Score = 9, Tags = new List<string> { "a", "b" } },
        };

        ulong written = await Bulk.BulkCopy.InsertMultipleCopyAsync(new[] { item }, Connection);
        Assert.That(written, Is.EqualTo(1UL));

        var rows = await TestJsonItem.Query(x => x.Id == id).WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1));

        // Raw jsonb survives (whitespace-normalized by PostgreSQL, so compare via parse).
        using var doc = JsonDocument.Parse(rows[0].RawData!);
        Assert.That(doc.RootElement.GetProperty("k").GetString(), Is.EqualTo("v"));
        Assert.That(doc.RootElement.GetProperty("n").GetInt32(), Is.EqualTo(7));

        // Typed jsonb deserializes back through the AOT context.
        Assert.That(rows[0].Payload, Is.Not.Null);
        Assert.That(rows[0].Payload!.Title, Is.EqualTo("title"));
        Assert.That(rows[0].Payload!.Score, Is.EqualTo(9));
        Assert.That(rows[0].Payload!.Tags, Is.EquivalentTo(new[] { "a", "b" }));
    }

    [Test]
    public async Task BulkCopy_NullColumns_RoundTrip()
    {
        var id = Guid.NewGuid();
        var item = new TestJsonItem { Id = id, Name = "copy-null", RawData = null, Payload = null };

        ulong written = await Bulk.BulkCopy.InsertMultipleCopyAsync(new[] { item }, Connection);
        Assert.That(written, Is.EqualTo(1UL));

        var rows = await TestJsonItem.Query(x => x.Id == id).WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].RawData, Is.Null, "NULL raw-JSON must COPY as a SQL NULL, not the text 'null'");
        Assert.That(rows[0].Payload, Is.Null);
    }

    [Test]
    public async Task BulkCopy_EncryptedColumns_RoundTrip()
    {
        var owner = $"copy-enc-{Guid.NewGuid():N}";
        var token = Guid.NewGuid();
        var issued = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var items = new[]
        {
            new TestSecret { Id = Guid.NewGuid(), Owner = owner, Ssn = "123-45-6789", Pin = 4242, Token = token, IssuedAt = issued, Note = "hush", Manual = "m1" },
            new TestSecret { Id = Guid.NewGuid(), Owner = owner, Ssn = "987-65-4321", Pin = 7,    Token = Guid.NewGuid(), IssuedAt = issued, Note = null,   Manual = "m2" },
        };

        // COPY runs each value through the same InsertColumnDescriptor.GetValue as the parameterized path,
        // so [Encrypted] columns are written as ciphertext bytea exactly as a normal insert would.
        ulong written = await Bulk.BulkCopy.InsertMultipleCopyAsync(items, Connection);
        Assert.That(written, Is.EqualTo(2UL));

        // Read back: the generated materializer decrypts via the same ambient encryptor.
        var rows = await TestSecret.Query(x => x.Owner == owner).WithConnection(Connection).ExecuteAsync().ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(2));

        var withNote = rows.Single(r => r.Ssn == "123-45-6789");
        Assert.That(withNote.Pin, Is.EqualTo(4242), "encrypted int round-trips through COPY");
        Assert.That(withNote.Token, Is.EqualTo(token), "encrypted Guid round-trips through COPY");
        Assert.That(withNote.Note, Is.EqualTo("hush"));

        var nullNote = rows.Single(r => r.Ssn == "987-65-4321");
        Assert.That(nullNote.Pin, Is.EqualTo(7));
        Assert.That(nullNote.Note, Is.Null, "a null [Encrypted] column must COPY as SQL NULL and read back null");
    }

    [Test]
    public async Task BulkCopy_ValueConvertor_AppliedOnWrite()
    {
        // UpperCaseStringConvertor uppercases on write; COPY must run the same convertor as a normal insert
        // (the value flows through the same InsertColumnDescriptor.GetValue), so the DB stores upper-case.
        var items = new[]
        {
            new TestConvertorItem { Id = Guid.NewGuid(), Label = "alpha", Value = 1 },
            new TestConvertorItem { Id = Guid.NewGuid(), Label = "beta", Value = 2 },
        };
        var ids = items.Select(i => i.Id).ToHashSet();

        ulong written = await Bulk.BulkCopy.InsertMultipleCopyAsync(items, Connection);
        Assert.That(written, Is.EqualTo(2UL));

        var rows = (await TestConvertorItem.Query(x => x.Value > 0).WithConnection(Connection).ExecuteAsync().ToListAsync())
            .Where(r => ids.Contains(r.Id)).ToList();
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows.Select(r => r.Label), Is.EquivalentTo(new[] { "ALPHA", "BETA" }),
            "the value convertor must be applied during COPY, not bypassed");
    }

    [Test]
    public async Task BulkCopy_WithinTransaction_RollbackUndoesInsert()
    {
        var name = $"copy-tx-{Guid.NewGuid():N}";

        await using (var tx = await Connection.BeginTransactionAsync())
        {
            ulong written = await Bulk.BulkCopy.InsertMultipleCopyAsync(
                new[] { new TestItem { Id = Guid.NewGuid(), Name = name, Priority = 1 } }, Connection, tx);
            Assert.That(written, Is.EqualTo(1UL));
            // Visible inside the transaction (same connection)...
            Assert.That(await Procedures.CountItemsByName(Connection, name), Is.EqualTo(1));
            await tx.RollbackAsync();
        }

        // ...and gone after rollback — proving the COPY participated in the transaction.
        Assert.That(await Procedures.CountItemsByName(Connection, name), Is.EqualTo(0));
    }

    [Test]
    public async Task DynamicTable_BulkCopy_InsertsAndRoundTrips()
    {
        // Exercises DynamicTable<T>.InsertMultipleCopyAsync against a runtime-named [TableType] table —
        // a separate code path from the BulkCopy<T> static entrypoint.
        const string table = "rt_audit_copy";
        var dt = AuditEntry.WithTableName(table).WithConnection(Connection);
        await dt.InstantiateAsync();
        try
        {
            var user = Guid.NewGuid();
            // Binary COPY is strict about types (no implicit cast like the parameterized path), so a DateTime
            // written to a 'timestamp without time zone' column must be Kind=Unspecified, not Utc.
            var at = new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Unspecified);
            var rows = new[]
            {
                new AuditEntry { Id = Guid.NewGuid(), UserId = user, Action = "a", Amount = 10, At = at },
                new AuditEntry { Id = Guid.NewGuid(), UserId = user, Action = "b", Amount = 20, At = at },
                new AuditEntry { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Action = "c", Amount = 30, At = at },
            };

            ulong written = await AuditEntry.WithTableName(table).WithConnection(Connection).InsertMultipleCopyAsync(rows);
            Assert.That(written, Is.EqualTo(3UL));

            Assert.That(await AuditEntry.WithTableName(table).WithConnection(Connection).CountAsync(), Is.EqualTo(3));
            Assert.That(await AuditEntry.WithTableName(table).WithConnection(Connection)
                .Query(x => x.UserId == user).SumAsync<int>(x => x.Amount), Is.EqualTo(30));
        }
        finally
        {
            await AuditEntry.WithTableName(table).WithConnection(Connection).DeleteInstanceAsync();
        }
    }
}
