using System;
using System.Linq;
using NUnit.Framework;
using Socigy.OpenSource.DB.Core.CommandBuilders;

namespace Socigy.OpenSource.DB.UnitTests;

/// <summary>
/// How an <see cref="InsertFields"/> option resolves to a set of written columns.
///
/// <see cref="InsertFields.ServerDefaults"/> omits every <c>[Default]</c> column unconditionally. That is
/// documented and deliberate, but it is opt-out at a distance: adding <c>[Default]</c> to an existing column
/// changes every insert that does not name it in <c>keep</c>, with no compile error, no runtime error and no
/// log line — the row is simply written with the database default instead of the value the application set.
/// <see cref="InsertFields.ServerDefaultsWhenUnset"/> is the value-driven alternative, added without touching
/// the existing behaviour or the per-batch fast path.
/// </summary>
[TestFixture]
public class InsertFieldsResolverTests
{
    private sealed class Row
    {
        public Guid Id { get; set; }
        public long Seq { get; set; }
        public bool IsAutoManaged { get; set; }
        public DateTime OccurredAt { get; set; }
        public string? Note { get; set; }
    }

    private static InsertColumnDescriptor Column(string name, Type type, Func<Row, object?> read, bool hasDbDefault)
        => new("@" + name, type, isJson: false, getValue: o => read((Row)o), isEncrypted: false, hasDbDefault: hasDbDefault);

    // A typical outbox/audit row: an id the application always sets, and four [Default] columns.
    private static InsertColumnDescriptor[] Plan() =>
    [
        Column("id", typeof(Guid), r => r.Id, hasDbDefault: false),
        Column("seq", typeof(long), r => r.Seq, hasDbDefault: true),
        Column("is_auto_managed", typeof(bool), r => r.IsAutoManaged, hasDbDefault: true),
        Column("occurred_at", typeof(DateTime), r => r.OccurredAt, hasDbDefault: true),
        Column("note", typeof(string), r => r.Note, hasDbDefault: true),
    ];

    private static string[] Names(InsertColumnDescriptor[] cols)
        => cols.Select(c => c.ParameterName.Substring(1)).ToArray();

    // ── the existing behaviour, unchanged ──

    [Test]
    public void ServerDefaults_still_omits_every_default_column_whatever_the_row_holds()
    {
        var row = new Row { Id = Guid.NewGuid(), Seq = 42, IsAutoManaged = true, OccurredAt = DateTime.UtcNow, Note = "set" };

        var resolved = InsertFieldsResolver.Resolve(Plan(), InsertFields.ServerDefaults, null, sample: null!);

        Assert.That(Names(resolved), Is.EqualTo(new[] { "id" }),
            "unconditional omission is what ServerDefaults means; changing it would silently alter every existing caller");
        Assert.That(row.Seq, Is.EqualTo(42), "resolution does not touch the row");
    }

    [Test]
    public void ServerDefaults_is_not_row_dependent_so_one_plan_serves_a_whole_batch()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InsertFieldsResolver.IsRowDependent(InsertFields.Default), Is.False);
            Assert.That(InsertFieldsResolver.IsRowDependent(InsertFields.IncludeAutoIncrement), Is.False);
            Assert.That(InsertFieldsResolver.IsRowDependent(InsertFields.ServerDefaults), Is.False);
            Assert.That(InsertFieldsResolver.IsRowDependent(InsertFields.ServerDefaultsWhenUnset), Is.True,
                "only the value-driven mode needs per-row resolution");
        });
    }

    // ── the new mode ──

    [Test]
    public void WhenUnset_writes_the_values_the_caller_set_and_omits_the_rest()
    {
        var row = new Row
        {
            Id = Guid.NewGuid(),
            Seq = 42,                      // set   -> written
            IsAutoManaged = true,          // set   -> written
            OccurredAt = default,          // unset -> server default
            Note = null,                   // unset -> server default
        };

        var resolved = InsertFieldsResolver.ResolveForRow(Plan(), row, kept: null);

        Assert.That(Names(resolved), Is.EqualTo(new[] { "id", "seq", "is_auto_managed" }));
    }

    // A value that happens to equal the CLR default — seq = 0, IsAutoManaged = false,
    // occurred_at = default(DateTime) — stays server-filled. This is the one case the two modes agree on,
    // and the limit of what any runtime check can decide.
    [Test]
    public void WhenUnset_omits_a_default_column_the_caller_left_at_its_type_default()
    {
        var row = new Row { Id = Guid.NewGuid(), Seq = 0, IsAutoManaged = false, OccurredAt = default, Note = null };

        Assert.That(Names(InsertFieldsResolver.ResolveForRow(Plan(), row, kept: null)), Is.EqualTo(new[] { "id" }));
    }

    [Test]
    public void Keep_still_forces_a_column_in_regardless_of_its_value()
    {
        var row = new Row { Id = Guid.NewGuid(), OccurredAt = default };
        var kept = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal) { "occurred_at" };

        Assert.That(Names(InsertFieldsResolver.ResolveForRow(Plan(), row, kept)),
            Does.Contain("occurred_at"),
            "an explicitly kept column is the caller's to supply, even when it holds the type default");
    }

    // Rows that agree share a plan; rows that disagree do not. This is what the batch grouping keys on.
    [Test]
    public void Rows_that_write_the_same_columns_share_a_shape_key()
    {
        var a = new Row { Id = Guid.NewGuid(), Seq = 1 };
        var b = new Row { Id = Guid.NewGuid(), Seq = 2 };
        var c = new Row { Id = Guid.NewGuid(), Seq = 3, Note = "different shape" };

        string ka = InsertFieldsResolver.RowShapeKey(InsertFieldsResolver.ResolveForRow(Plan(), a, null));
        string kb = InsertFieldsResolver.RowShapeKey(InsertFieldsResolver.ResolveForRow(Plan(), b, null));
        string kc = InsertFieldsResolver.RowShapeKey(InsertFieldsResolver.ResolveForRow(Plan(), c, null));

        Assert.Multiple(() =>
        {
            Assert.That(kb, Is.EqualTo(ka), "a uniform batch must still cost exactly one plan");
            Assert.That(kc, Is.Not.EqualTo(ka));
        });
    }

    // ── the judgement the mode rests on ──

    [TestCase(typeof(int), 0, true)]
    [TestCase(typeof(int), 7, false)]
    [TestCase(typeof(long), 0L, true)]
    [TestCase(typeof(bool), false, true)]
    [TestCase(typeof(bool), true, false)]
    [TestCase(typeof(string), null, true)]
    [TestCase(typeof(string), "", false)]
    [TestCase(typeof(string), "x", false)]
    [TestCase(typeof(double), 0.0, true)]
    public void IsClrDefault_recognises_the_type_default(Type type, object? value, bool expected)
    {
        Assert.That(InsertFieldsResolver.IsClrDefault(type, value), Is.EqualTo(expected));
    }

    [Test]
    public void IsClrDefault_handles_the_struct_types_a_default_column_usually_has()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InsertFieldsResolver.IsClrDefault(typeof(Guid), Guid.Empty), Is.True);
            Assert.That(InsertFieldsResolver.IsClrDefault(typeof(Guid), Guid.NewGuid()), Is.False);
            Assert.That(InsertFieldsResolver.IsClrDefault(typeof(DateTime), default(DateTime)), Is.True);
            Assert.That(InsertFieldsResolver.IsClrDefault(typeof(DateTime), new DateTime(2026, 1, 1)), Is.False);
            Assert.That(InsertFieldsResolver.IsClrDefault(typeof(DateTimeOffset), default(DateTimeOffset)), Is.True);
            Assert.That(InsertFieldsResolver.IsClrDefault(typeof(TimeSpan), TimeSpan.Zero), Is.True);
            Assert.That(InsertFieldsResolver.IsClrDefault(typeof(decimal), 0m), Is.True);
        });
    }

    private enum State { Pending = 0, Active = 1 }

    [Test]
    public void IsClrDefault_treats_an_enums_zero_member_as_unset()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InsertFieldsResolver.IsClrDefault(typeof(State), State.Pending), Is.True);
            Assert.That(InsertFieldsResolver.IsClrDefault(typeof(State), State.Active), Is.False);
        });
    }

    [Test]
    public void A_nullable_value_type_holding_its_underlying_default_counts_as_set()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InsertFieldsResolver.IsClrDefault(typeof(int?), null), Is.True);
            Assert.That(InsertFieldsResolver.IsClrDefault(typeof(int?), 0), Is.False,
                "a nullable column can say 'unset' properly, so an explicit 0 means the caller chose 0");
        });
    }

    // Omitting a column the caller may have populated is the damaging direction, so an unrecognised struct
    // is treated as set rather than guessed at.
    private struct Exotic { public int A; }

    [Test]
    public void An_unrecognised_struct_is_treated_as_set()
    {
        Assert.That(InsertFieldsResolver.IsClrDefault(typeof(Exotic), default(Exotic)), Is.False);
    }
}
