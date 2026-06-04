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

        [Test]
        public void LongValue_IsTruncated()
        {
            var cmd = CommandWith(("@p0", new string('x', 1000)));
            var options = new SocigyDbDiagnosticsOptions { CaptureParameterValues = true, MaxParameterValueLength = 16 };

            string rendered = ParameterSerializer.Serialize(cmd.Parameters, options);

            Assert.That(rendered, Does.Contain("(truncated)"));
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
