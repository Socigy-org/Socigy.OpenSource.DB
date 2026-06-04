using System.Diagnostics;
using System.Diagnostics.Metrics;

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

        internal static readonly string Version =
            typeof(SocigyDbInstrumentation).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        internal static readonly ActivitySource ActivitySource = new ActivitySource(ActivitySourceName, Version);

        internal static readonly Meter Meter = new Meter(MeterName, Version);

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
