using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Socigy.OpenSource.DB.SourceGenerator.Tests;

/// <summary>
/// SCGDB027 — an insert that lets the server fill a <c>[Default]</c> column the row may carry a value for.
///
/// <para>
/// These run the generator first and then the analyzer over the <b>augmented</b> compilation, which is the
/// only way to test what a consumer actually gets: the methods being flagged are emitted by the generator, so
/// a test that skips generation is testing a compilation where they do not exist.
/// </para>
/// <para>
/// The call-shape matrix below is the point of this fixture. The first version of this diagnostic recognised
/// only two shapes and fired on none of the call sites in a real application, because it inspected syntax
/// rather than bound symbols — and it passed its tests, because the fixtures had been written to match the
/// implementation. Every shape the library offers is enumerated here, and a new one is a test before it is
/// a feature.
/// </para>
/// </summary>
[TestFixture]
public class ServerDefaultsAnalyzerTests
{
    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using System.Data.Common;
        using System.Threading.Tasks;
        using Socigy.OpenSource.DB.Attributes;
        using Socigy.OpenSource.DB.Core.Bulk;
        using Socigy.OpenSource.DB.Core.CommandBuilders;
        using Ctx = Socigy.OpenSource.DB.Identity.Context;
        namespace Sample
        {
            [Table("outbox")]
            public partial class Outbox
            {
                [PrimaryKey] public Guid Id { get; set; }
                [Default] public DateTime OccurredAt { get; set; }
                [Default] public long Seq { get; set; }
                public string Payload { get; set; } = "";
            }

            public static class InsertDefaults
            {
                // The "shared constant" spelling: most call sites name this rather than the enum member.
                public const InsertFields ServerFilled = InsertFields.ServerDefaults;
                public static readonly InsertFields ServerFilledReadonly = InsertFields.ServerDefaults;
            }

            public static class Caller
            {
                public static async Task Run(DbConnection conn, Ctx.IIdentity d, Outbox row, IEnumerable<Outbox> rows)
                {
        """;

    private const string Postamble = """
                }
            }
        }
        """;

    private static ImmutableArray<Diagnostic> Analyze(string body)
    {
        var (augmented, _) = GeneratorTestHarness.Run(
            Preamble + body + Postamble, GeneratorTestHarness.NoWebJson);

        return augmented
            .WithAnalyzers([new ServerDefaultsAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static string[] Messages(string body)
        => Analyze(body).Where(d => d.Id == "SCGDB027").Select(d => d.GetMessage()).ToArray();

    // ── every call shape the library offers ────────────────────────────────────────────────────────────

    [TestCase("await d.Outboxes.InsertAsync(row, InsertFields.ServerDefaults);",
        TestName = "context_set_single")]
    [TestCase("await d.Outboxes.InsertMultipleAsync(new[] { row }, InsertFields.ServerDefaults);",
        TestName = "context_set_multiple")]
    [TestCase("await Outbox.InsertMultipleAsync(new[] { row }, conn, null, InsertFields.ServerDefaults);",
        TestName = "static_on_row_class")]
    [TestCase("await BulkCopy.InsertMultipleCopyAsync(new[] { row }, conn, null, InsertFields.ServerDefaults);",
        TestName = "bulk_copy")]
    [TestCase("await row.Insert().ExcludeAutoFields().WithConnection(conn).ExecuteAsync();",
        TestName = "fluent_builder")]
    [TestCase("await d.Outboxes.InsertAsync(row, InsertDefaults.ServerFilled);",
        TestName = "shared_const")]
    [TestCase("await d.Outboxes.InsertAsync(row, InsertDefaults.ServerFilledReadonly);",
        TestName = "shared_static_readonly")]
    [TestCase("var f = InsertFields.ServerDefaults; await d.Outboxes.InsertAsync(row, f);",
        TestName = "local_variable")]
    public void Every_shape_that_omits_a_default_column_is_reported(string body)
    {
        var messages = Messages(body);

        Assert.That(messages, Is.Not.Empty, "this call omits OccurredAt and Seq without naming them in keep");
        Assert.That(messages[0], Does.Contain("OccurredAt").And.Contain("Seq"));
    }

    // The rows argument is an array too. Reading it as the keep list is what silently suppressed the
    // diagnostic on every batch insert in the first implementation.
    [TestCase("await Outbox.InsertMultipleAsync(new[] { row }, conn, null, InsertFields.ServerDefaults);",
        TestName = "rows_array_of_variables")]
    [TestCase("await Outbox.InsertMultipleAsync(rows, conn, null, InsertFields.ServerDefaults);",
        TestName = "rows_as_enumerable_variable")]
    [TestCase("await d.Outboxes.InsertMultipleAsync(new List<Outbox> { row }, InsertFields.ServerDefaults);",
        TestName = "rows_as_list_initializer")]
    public void The_rows_argument_is_never_mistaken_for_the_keep_list(string body)
        => Assert.That(Messages(body), Is.Not.Empty);

    // ── keep is honoured ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public void A_keep_naming_every_default_column_reports_nothing()
        => Assert.That(Messages(
            "await d.Outboxes.InsertAsync(row, new[] { nameof(Outbox.Seq), nameof(Outbox.OccurredAt) });"),
            Is.Empty);

    [Test]
    public void A_partial_keep_reports_only_what_it_does_not_name()
    {
        var messages = Messages("await d.Outboxes.InsertAsync(row, new[] { nameof(Outbox.Seq) });");

        Assert.That(messages, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            Assert.That(messages[0], Does.Contain("OccurredAt"), "not kept, so it takes the database default");
            Assert.That(messages[0], Does.Not.Contain("Seq"), "kept, so the caller's value is written");
        });
    }

    // The keepColumns overloads declare `fields = ServerDefaults`, so an omitted argument still omits
    // columns. Reading "not supplied" as "InsertFields.Default" would silently stop reporting these.
    [Test]
    public void A_keepColumns_overload_with_fields_omitted_is_still_reported()
    {
        var messages = Messages("await d.Outboxes.InsertAsync(row, new[] { nameof(Outbox.Seq) });");

        Assert.That(messages, Is.Not.Empty, "fields defaults to ServerDefaults on this overload");
        Assert.That(messages[0], Does.Contain("OccurredAt").And.Not.Contain("Seq"));
    }

    // The remedy has to actually compose with keep, or promoting a call site silences the warning
    // without changing what is written.
    [Test]
    public void ServerDefaultsWhenUnset_combined_with_keep_reports_nothing()
        => Assert.That(Messages(
            "await d.Outboxes.InsertAsync(row, InsertFields.ServerDefaultsWhenUnset, r => new object?[] { r.Seq });"),
            Is.Empty);

    [Test]
    public void ServerDefaultsWhenUnset_combined_with_a_string_keep_reports_nothing()
        => Assert.That(Messages(
            "await d.Outboxes.InsertAsync(row, new[] { nameof(Outbox.Seq) }, InsertFields.ServerDefaultsWhenUnset);"),
            Is.Empty);

    [Test]
    public void A_keep_naming_the_database_column_name_is_honoured_too()
        => Assert.That(Messages(
            "await d.Outboxes.InsertAsync(row, new[] { \"seq\", \"occurred_at\" });"),
            Is.Empty);

    [Test]
    public void An_expression_keep_selector_is_honoured()
        => Assert.That(Messages(
            "await d.Outboxes.InsertAsync(row, InsertFields.ServerDefaults, r => new object?[] { r.Seq, r.OccurredAt });"),
            Is.Empty);

    // A keep whose contents cannot be read must not be guessed at: the developer may well have named
    // every column, and a false positive in an audit costs more than a miss.
    [Test]
    public void A_keep_supplied_as_a_variable_is_left_alone()
        => Assert.That(Messages(
            "var keep = new[] { nameof(Outbox.Seq), nameof(Outbox.OccurredAt) };\n" +
            "await d.Outboxes.InsertAsync(row, keep);"),
            Is.Empty);

    // ── calls that must stay silent ────────────────────────────────────────────────────────────────────

    [TestCase("await d.Outboxes.InsertAsync(row);", TestName = "fields_left_at_default")]
    [TestCase("await d.Outboxes.InsertAsync(row, InsertFields.Default);", TestName = "explicit_Default")]
    [TestCase("await d.Outboxes.InsertAsync(row, InsertFields.IncludeAutoIncrement);", TestName = "IncludeAutoIncrement")]
    [TestCase("await d.Outboxes.InsertAsync(row, InsertFields.ServerDefaultsWhenUnset);", TestName = "the_remedy_itself")]
    [TestCase("await d.Outboxes.DeleteAsync(row);", TestName = "not_an_insert")]
    public void Calls_that_do_not_discard_a_value_report_nothing(string body)
        => Assert.That(Messages(body), Is.Empty);

    [Test]
    public void A_table_without_default_columns_is_never_flagged()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using Socigy.OpenSource.DB.Attributes;
            using Socigy.OpenSource.DB.Core.CommandBuilders;
            namespace Sample
            {
                [Table("plain")] public partial class Plain { [PrimaryKey] public Guid Id { get; set; } }
                public static class Caller
                {
                    public static async Task Run(Socigy.OpenSource.DB.Identity.Context.IIdentity d, Plain row)
                        => await d.Plains.InsertAsync(row, InsertFields.ServerDefaults);
                }
            }
            """;
        var (augmented, _) = GeneratorTestHarness.Run(source, GeneratorTestHarness.NoWebJson);
        var diagnostics = augmented.WithAnalyzers([new ServerDefaultsAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.That(diagnostics.Where(d => d.Id == "SCGDB027"), Is.Empty);
    }

    // An [AutoIncrement] column is the database's to generate; flagging it would bury the signal.
    [Test]
    public void An_auto_increment_column_is_not_reported()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using Socigy.OpenSource.DB.Attributes;
            using Socigy.OpenSource.DB.Core.CommandBuilders;
            namespace Sample
            {
                [Table("tickets")]
                public partial class Ticket
                {
                    [PrimaryKey] public Guid Id { get; set; }
                    [AutoIncrement, Default] public long Number { get; set; }
                }
                public static class Caller
                {
                    public static async Task Run(Socigy.OpenSource.DB.Identity.Context.IIdentity d, Ticket row)
                        => await d.Tickets.InsertAsync(row, InsertFields.ServerDefaults);
                }
            }
            """;
        var (augmented, _) = GeneratorTestHarness.Run(source, GeneratorTestHarness.NoWebJson);
        var diagnostics = augmented.WithAnalyzers([new ServerDefaultsAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.That(diagnostics.Where(d => d.Id == "SCGDB027"), Is.Empty);
    }

    [Test]
    public void The_diagnostic_defaults_to_Info_so_a_normal_build_stays_quiet()
    {
        var diagnostic = Analyze("await d.Outboxes.InsertAsync(row, InsertFields.ServerDefaults);")
            .First(d => d.Id == "SCGDB027");

        Assert.That(diagnostic.Severity, Is.EqualTo(DiagnosticSeverity.Info));
    }
}
