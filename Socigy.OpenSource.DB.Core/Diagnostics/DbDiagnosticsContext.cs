using Microsoft.Extensions.Logging;

namespace Socigy.OpenSource.DB.Core.Diagnostics
{
#nullable enable
    /// <summary>
    /// Per-context carrier for diagnostics state sourced from dependency injection (an
    /// <see cref="ILogger"/> and, optionally, an overriding <see cref="SocigyDbDiagnosticsOptions"/>).
    /// The generated database context/factory builds one from DI and flows it into command builders via
    /// <c>WithDiagnostics(...)</c>, so logging works for DI consumers without calling
    /// <see cref="SocigyDbDiagnostics.Configure"/>. When a builder has no context, execution falls back
    /// to the ambient <see cref="SocigyDbDiagnostics.Options"/>.
    /// </summary>
    public sealed class DbDiagnosticsContext
    {
        /// <summary>Constructs a carrier. The logger is created from <paramref name="loggerFactory"/> when provided.</summary>
        public DbDiagnosticsContext(ILoggerFactory? loggerFactory = null, SocigyDbDiagnosticsOptions? options = null)
        {
            Options = options;
            Logger = loggerFactory?.CreateLogger("Socigy.OpenSource.DB.Sql");
        }

        /// <summary>An explicit logger for this context, or <see langword="null"/> to fall back to the ambient logger.</summary>
        public ILogger? Logger { get; }

        /// <summary>Overriding options for this context, or <see langword="null"/> to use the ambient options.</summary>
        public SocigyDbDiagnosticsOptions? Options { get; }

        /// <summary>Resolves the effective options: this context's, else the ambient ones.</summary>
        internal SocigyDbDiagnosticsOptions EffectiveOptions => Options ?? SocigyDbDiagnostics.Options;

        /// <summary>Resolves the effective logger: this context's, else the ambient one.</summary>
        internal ILogger? EffectiveLogger => Logger ?? SocigyDbDiagnostics.GetLogger();
    }
#nullable disable
}
