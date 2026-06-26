using System;
using Microsoft.Extensions.Logging;

namespace Socigy.OpenSource.DB.Core.Diagnostics
{
#nullable enable
    /// <summary>
    /// Global, ambient configuration for SQL logging and parameter capture. Tracing and metrics need
    /// no configuration (they emit whenever an OpenTelemetry listener subscribes to
    /// <see cref="SocigyDbInstrumentation.ActivitySourceName"/> / <see cref="SocigyDbInstrumentation.MeterName"/>);
    /// only logging and the sensitive parameter-value capture are configured here, because most SQL is
    /// executed from static generated methods that have no access to dependency injection.
    /// </summary>
    public sealed class SocigyDbDiagnosticsOptions
    {
        /// <summary>
        /// When set, a logger named <c>"Socigy.OpenSource.DB.Sql"</c> is created from this factory and
        /// used to emit one structured message per executed command. When <see langword="null"/>, no
        /// logging is performed (tracing/metrics are unaffected).
        /// </summary>
        public ILoggerFactory? LoggerFactory { get; set; }

        /// <summary>
        /// Whether the SQL command text is captured on spans and logs. Default <see langword="true"/>.
        /// The command text never contains parameter values (those are bound separately).
        /// </summary>
        public bool CaptureCommandText { get; set; } = true;

        /// <summary>
        /// SENSITIVE. Whether parameter <em>values</em> are captured. Default <see langword="false"/> —
        /// only parameter names and DB types are recorded. Enable only in trusted environments, ideally
        /// together with <see cref="RedactParameter"/>, since values may contain PII or secrets.
        /// Mirrors EF Core's <c>EnableSensitiveDataLogging</c>.
        /// </summary>
        public bool CaptureParameterValues { get; set; } = false;

        /// <summary>Maximum rendered length of a single captured parameter value before truncation. Default 256.</summary>
        public int MaxParameterValueLength { get; set; } = 256;

        /// <summary>Log level used for the per-command structured message. Default <see cref="LogLevel.Debug"/>.</summary>
        public LogLevel LogLevel { get; set; } = LogLevel.Debug;

        /// <summary>
        /// When set, any command whose execution exceeds this many milliseconds is reported as a slow query,
        /// independently of <see cref="LogLevel"/>: a one-off <see cref="LogLevel.Warning"/> message is logged,
        /// the span is tagged <c>db.query.slow=true</c>, and the <c>socigy.db.slow_queries</c> counter is
        /// incremented. <see langword="null"/> (the default) disables slow-query detection. This catches
        /// regressions even when per-command <see cref="LogLevel.Debug"/> logging is off in production.
        /// </summary>
        public double? SlowQueryThresholdMs { get; set; }

        /// <summary>
        /// Optional redaction hook invoked per parameter when <see cref="CaptureParameterValues"/> is
        /// enabled. Receives the parameter name and raw value, returns the string to record (return
        /// <c>"***"</c> to mask). When <see langword="null"/>, values are rendered and truncated directly.
        /// </summary>
        public Func<string, object?, string?>? RedactParameter { get; set; }
    }
#nullable disable
}
