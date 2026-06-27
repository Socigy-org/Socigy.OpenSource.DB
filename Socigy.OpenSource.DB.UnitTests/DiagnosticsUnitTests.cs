using System;
using Npgsql;
using Socigy.OpenSource.DB.Core.Diagnostics;

namespace Socigy.OpenSource.DB.UnitTests
{
    /// <summary>No-database tests for parameter rendering and the sensitive-value capture gate.</summary>
    [TestFixture]
    public class DiagnosticsUnitTests
    {
        private static NpgsqlCommand CommandWith(params (string Name, object Value)[] parameters)
        {
            var cmd = new NpgsqlCommand();
            foreach (var (name, value) in parameters)
                cmd.Parameters.Add(new NpgsqlParameter(name, value));
            return cmd;
        }

        [Test]
        public void CaptureOff_OmitsValues()
        {
            var cmd = CommandWith(("@p0", "super-secret"));
            var options = new SocigyDbDiagnosticsOptions { CaptureParameterValues = false };

            string rendered = ParameterSerializer.Serialize(cmd.Parameters, options);

            Assert.That(rendered, Does.Contain("@p0="));
            Assert.That(rendered, Does.Not.Contain("super-secret"));
        }

        [Test]
        public void CaptureOn_IncludesValues()
        {
            var cmd = CommandWith(("@p0", "hello"));
            var options = new SocigyDbDiagnosticsOptions { CaptureParameterValues = true };

            string rendered = ParameterSerializer.Serialize(cmd.Parameters, options);

            Assert.That(rendered, Does.Contain("@p0=hello"));
        }

        [Test]
        public void RedactionHook_MasksMatchingParameters()
        {
            var cmd = CommandWith(("@password", "p@ss"), ("@name", "alice"));
            var options = new SocigyDbDiagnosticsOptions
            {
                CaptureParameterValues = true,
                RedactParameter = (name, value) =>
                    name.Contains("password", StringComparison.OrdinalIgnoreCase) ? "***" : value?.ToString()
            };

            string rendered = ParameterSerializer.Serialize(cmd.Parameters, options);

            Assert.That(rendered, Does.Contain("@password=***"));
            Assert.That(rendered, Does.Not.Contain("p@ss"));
            Assert.That(rendered, Does.Contain("@name=alice"));
        }

        private sealed class ThrowsOnToString { public override string ToString() => throw new InvalidOperationException("boom"); }

        // Diagnostics must never throw into the query path: a redaction hook or a value whose ToString throws is
        // rendered as a placeholder, not propagated (which would crash an already-successful command).
        [Test]
        public void Throwing_redaction_hook_does_not_throw()
        {
            var cmd = CommandWith(("@p0", "x"));
            var options = new SocigyDbDiagnosticsOptions
            {
                CaptureParameterValues = true,
                RedactParameter = (_, __) => throw new InvalidOperationException("boom"),
            };
            string rendered = ParameterSerializer.Serialize(cmd.Parameters, options);
            Assert.That(rendered, Does.Contain("<unrenderable>"));
        }

        [Test]
        public void Throwing_value_ToString_does_not_throw()
        {
            var cmd = new NpgsqlCommand();
            cmd.Parameters.Add(new NpgsqlParameter("@p0", new ThrowsOnToString()));
            string rendered = ParameterSerializer.Serialize(cmd.Parameters,
                new SocigyDbDiagnosticsOptions { CaptureParameterValues = true });
            Assert.That(rendered, Does.Contain("<unrenderable>"));
        }

        // An array / collection (= ANY(@p)) parameter renders its contents, not "System.Int32[]".
        [Test]
        public void Array_parameter_renders_contents()
        {
            var cmd = CommandWith(("@p0", new[] { 1, 2, 3 }));
            string rendered = ParameterSerializer.Serialize(cmd.Parameters,
                new SocigyDbDiagnosticsOptions { CaptureParameterValues = true, MaxParameterValueLength = 256 });
            Assert.That(rendered, Does.Contain("[1, 2, 3]").And.Not.Contain("System.Int32[]"));
        }

        [Test]
        public void DateTime_parameter_renders_round_trip()
        {
            var cmd = CommandWith(("@p0", new DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc)));
            string rendered = ParameterSerializer.Serialize(cmd.Parameters,
                new SocigyDbDiagnosticsOptions { CaptureParameterValues = true, MaxParameterValueLength = 256 });
            Assert.That(rendered, Does.Contain("2026-06-27T10:00:00"));
        }

        [Test]
        public void LongValue_IsTruncated()
        {
            var cmd = CommandWith(("@p0", new string('x', 1000)));
            var options = new SocigyDbDiagnosticsOptions { CaptureParameterValues = true, MaxParameterValueLength = 16 };

            string rendered = ParameterSerializer.Serialize(cmd.Parameters, options);

            Assert.That(rendered, Does.Contain("(truncated)"));
        }

        [Test]
        public void RedactionHook_OutputIsAlsoLengthCapped()
        {
            // Regression: a redaction hook that echoes (or expands) the value bypassed MaxParameterValueLength,
            // so a hook returning a huge string could bloat every span/log line. The cap must apply to the
            // hook's output too.
            var cmd = CommandWith(("@p0", "ignored"));
            var options = new SocigyDbDiagnosticsOptions
            {
                CaptureParameterValues = true,
                MaxParameterValueLength = 16,
                RedactParameter = (name, value) => new string('y', 1000),
            };

            string rendered = ParameterSerializer.Serialize(cmd.Parameters, options);

            Assert.That(rendered, Does.Contain("(truncated)"));
            Assert.That(rendered.Length, Is.LessThan(100), "redacted output must be capped, not emitted in full");
        }

        [Test]
        public void RedactionHook_ShortOutputNotTruncated()
        {
            var cmd = CommandWith(("@password", "p@ss"));
            var options = new SocigyDbDiagnosticsOptions
            {
                CaptureParameterValues = true,
                MaxParameterValueLength = 16,
                RedactParameter = (name, value) => "***",
            };

            string rendered = ParameterSerializer.Serialize(cmd.Parameters, options);

            Assert.That(rendered, Is.EqualTo("@password=***"));
        }

        [Test]
        public void NoParameters_RendersNone()
        {
            var cmd = new NpgsqlCommand();
            string rendered = ParameterSerializer.Serialize(cmd.Parameters, new SocigyDbDiagnosticsOptions());
            Assert.That(rendered, Is.EqualTo("(none)"));
        }
    }
}
