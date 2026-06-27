using System;
using Microsoft.Extensions.Logging;

namespace Socigy.OpenSource.DB.Core.Diagnostics
{
#nullable enable
    /// <summary>
    /// Ambient entry point for configuring SQL logging and parameter capture. Set this once at startup
    /// (e.g. after the host is built so an <see cref="ILoggerFactory"/> is available). All static
    /// generated execution methods read this configuration, so logging works without threading a logger
    /// through every call site. When a <see cref="Context.SocigyDbScope"/> carries its own
    /// <see cref="DbDiagnosticsContext"/> (from DI), that takes precedence over this ambient config.
    /// </summary>
    public static class SocigyDbDiagnostics
    {
        private static volatile SocigyDbDiagnosticsOptions _options = new();

        /// <summary>The current ambient options. Never <see langword="null"/>.</summary>
        public static SocigyDbDiagnosticsOptions Options => _options;

        /// <summary>
        /// Replaces the ambient options atomically. Builds a fresh options instance, applies
        /// <paramref name="configure"/>, then swaps it in — readers on the hot path are lock-free.
        /// </summary>
        public static void Configure(Action<SocigyDbDiagnosticsOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var options = new SocigyDbDiagnosticsOptions();
            configure(options);
            _options = options;
        }

        // Cache the logger so we don't call CreateLogger per command. Re-created only when the factory changes.
        // volatile so the lock-free fast-path read can't observe the published _loggerFactoryCache while still
        // seeing a stale/null _loggerCache (writes are ordered _loggerCache then _loggerFactoryCache below, and
        // volatile gives the acquire/release pairing that returns a complete logger to a concurrent reader).
        private static volatile ILoggerFactory? _loggerFactoryCache;
        private static volatile ILogger? _loggerCache;
        private static readonly object _loggerLock = new();

        /// <summary>
        /// The cached ambient SQL logger (category <c>Socigy.OpenSource.DB.Sql</c>), or <see langword="null"/>
        /// when no <see cref="SocigyDbDiagnosticsOptions.LoggerFactory"/> is configured. Public so optional
        /// add-on packages can log background actions to the same pipeline.
        /// </summary>
        public static ILogger? GetLogger()
        {
            var factory = _options.LoggerFactory;
            if (factory == null) return null;

            if (!ReferenceEquals(factory, _loggerFactoryCache))
            {
                lock (_loggerLock)
                {
                    if (!ReferenceEquals(factory, _loggerFactoryCache))
                    {
                        _loggerCache = factory.CreateLogger("Socigy.OpenSource.DB.Sql");
                        _loggerFactoryCache = factory;
                    }
                }
            }
            return _loggerCache;
        }
    }
#nullable disable
}
