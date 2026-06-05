using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Socigy.OpenSource.DB.Core.Diagnostics
{
#nullable enable
    /// <summary>
    /// Central definition of the library's OpenTelemetry instruments. Consumers wire these up by
    /// passing <see cref="ActivitySourceName"/> to <c>AddSource(...)</c> and <see cref="MeterName"/>
    /// to <c>AddMeter(...)</c> on their tracer/meter providers. No further configuration is required
    /// for tracing or metrics — the <see cref="ActivitySource"/>/<see cref="Meter"/> emit automatically
    /// once a listener subscribes.
    /// </summary>
    public static class SocigyDbInstrumentation
    {
        /// <summary>The name to pass to <c>TracerProviderBuilder.AddSource(...)</c>.</summary>
        public const string ActivitySourceName = "Socigy.OpenSource.DB";

        /// <summary>The name to pass to <c>MeterProviderBuilder.AddMeter(...)</c>.</summary>
        public const string MeterName = "Socigy.OpenSource.DB";

        internal static readonly string Version = ResolveVersion();

        // Prefer the package/informational version (e.g. "0.2.1") over the 4-part assembly version, and
        // drop any SourceLink "+<git-hash>" suffix so the instrumentation scope reports a clean version.
        private static string ResolveVersion()
        {
            Assembly assembly = typeof(SocigyDbInstrumentation).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
            {
                int plus = informational!.IndexOf('+');
                return plus >= 0 ? informational.Substring(0, plus) : informational;
            }
            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        /// <summary>The library's <see cref="System.Diagnostics.ActivitySource"/>. Public so optional add-on
        /// packages (e.g. HashiCorp Vault) emit their background spans under the same source consumers already
        /// subscribe to via <c>AddSource("Socigy.OpenSource.DB")</c>.</summary>
        public static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

        /// <summary>The library's <see cref="System.Diagnostics.Metrics.Meter"/> (same name as <see cref="ActivitySource"/>).</summary>
        public static readonly Meter Meter = new(MeterName, Version);

        /// <summary>Duration of database client operations, in seconds (OTel semantic-conventions metric).</summary>
        internal static readonly Histogram<double> DurationHistogram = Meter.CreateHistogram<double>(
            "db.client.operation.duration", unit: "s", description: "Duration of database client operations.");

        /// <summary>Number of SQL commands executed.</summary>
        internal static readonly Counter<long> CommandCounter = Meter.CreateCounter<long>(
            "socigy.db.commands", unit: "{command}", description: "Number of SQL commands executed.");

        /// <summary>Number of SQL commands that threw during execution.</summary>
        internal static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "socigy.db.command.errors", unit: "{error}", description: "Number of SQL commands that threw.");
    }
#nullable disable
}
