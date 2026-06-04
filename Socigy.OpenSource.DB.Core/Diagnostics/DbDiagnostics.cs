using System;
using System.Data.Common;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Socigy.OpenSource.DB.Core.Diagnostics
{
#nullable enable
    /// <summary>
    /// The single seam every SQL execution site routes through. Wraps a command's execution to emit an
    /// OpenTelemetry <see cref="Activity"/> (with database semantic-convention tags), record the
    /// duration metric and command/error counters, and write one structured log message — capturing the
    /// SQL text and parameters (parameter values only when explicitly enabled). The actual ADO.NET call
    /// is supplied as a delegate so the concrete command type (e.g. <c>NpgsqlCommand</c>) is preserved
    /// at the call site.
    /// </summary>
    public static class DbDiagnostics
    {
        /// <summary>Instruments an <c>ExecuteNonQueryAsync</c> call.</summary>
        public static async Task<int> ExecuteNonQueryAsync(
            DbCommand command,
            string operation,
            Func<CancellationToken, Task<int>> execute,
            CancellationToken cancellationToken = default,
            DbDiagnosticsContext? diagnostics = null)
        {
            var scope = Scope.Start(command, operation, diagnostics);
            try
            {
                int affected = await execute(cancellationToken).ConfigureAwait(false);
                scope.SetRowsAffected(affected);
                scope.Complete();
                return affected;
            }
            catch (Exception ex)
            {
                scope.Fail(ex);
                throw;
            }
            finally
            {
                scope.Dispose();
            }
        }

        /// <summary>Instruments an <c>ExecuteScalarAsync</c> call.</summary>
        public static async Task<object?> ExecuteScalarAsync(
            DbCommand command,
            string operation,
            Func<CancellationToken, Task<object?>> execute,
            CancellationToken cancellationToken = default,
            DbDiagnosticsContext? diagnostics = null)
        {
            var scope = Scope.Start(command, operation, diagnostics);
            try
            {
                object? result = await execute(cancellationToken).ConfigureAwait(false);
                scope.Complete();
                return result;
            }
            catch (Exception ex)
            {
                scope.Fail(ex);
                throw;
            }
            finally
            {
                scope.Dispose();
            }
        }

        /// <summary>
        /// Instruments an <c>ExecuteReaderAsync</c> call. The returned <see cref="InstrumentedReader"/>
        /// owns both the reader and the span; the span stays open until the wrapper is disposed, so it
        /// spans the entire enumeration and records the number of rows read.
        /// </summary>
        public static async Task<InstrumentedReader> ExecuteReaderAsync(
            DbCommand command,
            string operation,
            Func<CancellationToken, Task<DbDataReader>> execute,
            CancellationToken cancellationToken = default,
            DbDiagnosticsContext? diagnostics = null)
        {
            var scope = Scope.Start(command, operation, diagnostics);
            try
            {
                DbDataReader reader = await execute(cancellationToken).ConfigureAwait(false);
                return new InstrumentedReader(reader, scope);
            }
            catch (Exception ex)
            {
                scope.Fail(ex);
                scope.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Holds the database state for a single instrumented command: the <see cref="Activity"/>, the
        /// elapsed timer, and the metric/log emission. Created via <see cref="Start"/>, finished exactly
        /// once via <see cref="Complete"/> or <see cref="Fail"/>.
        /// </summary>
        internal sealed class Scope
        {
            private readonly DbCommand _command;
            private readonly string _operation;
            private readonly Activity? _activity;
            private readonly Stopwatch _stopwatch;
            private readonly SocigyDbDiagnosticsOptions _options;
            private readonly ILogger? _logger;

            private int _rowsAffected = -1;
            private long _returnedRows = -1;
            private Exception? _error;
            private bool _finished;

            private Scope(DbCommand command, string operation, Activity? activity,
                SocigyDbDiagnosticsOptions options, ILogger? logger)
            {
                _command = command;
                _operation = operation;
                _activity = activity;
                _options = options;
                _logger = logger;
                _stopwatch = Stopwatch.StartNew();
            }

            public static Scope Start(DbCommand command, string operation, DbDiagnosticsContext? diagnostics)
            {
                SocigyDbDiagnosticsOptions options = diagnostics?.EffectiveOptions ?? SocigyDbDiagnostics.Options;
                ILogger? logger = diagnostics?.EffectiveLogger ?? SocigyDbDiagnostics.GetLogger();

                Activity? activity = SocigyDbInstrumentation.ActivitySource.StartActivity(
                    operation + " (postgresql)", ActivityKind.Client);

                if (activity != null && activity.IsAllDataRequested)
                {
                    activity.SetTag("db.system", "postgresql");
                    activity.SetTag("db.operation.name", operation);
                    if (options.CaptureCommandText)
                        activity.SetTag("db.query.text", command.CommandText);
                }

                return new Scope(command, operation, activity, options, logger);
            }

            public void SetRowsAffected(int rows) => _rowsAffected = rows;
            public void SetReturnedRows(long rows) => _returnedRows = rows;

            public void Complete()
            {
                if (_error != null) return;
                Finish();
            }

            public void Fail(Exception ex)
            {
                _error = ex;
                if (_activity != null)
                {
                    _activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                    // netstandard2.0: no Activity.AddException — record an OTel-style exception event manually.
                    _activity.AddEvent(new ActivityEvent("exception", default, new ActivityTagsCollection
                    {
                        { "exception.type", ex.GetType().FullName },
                        { "exception.message", ex.Message },
                        { "exception.stacktrace", ex.ToString() },
                    }));
                }
                SocigyDbInstrumentation.ErrorCounter.Add(1, OperationTag());
                Finish();
            }

            private void Finish()
            {
                if (_finished) return;
                _finished = true;

                _stopwatch.Stop();
                double seconds = _stopwatch.Elapsed.TotalSeconds;

                SocigyDbInstrumentation.DurationHistogram.Record(seconds, OperationTag());
                SocigyDbInstrumentation.CommandCounter.Add(1, OperationTag());

                if (_activity != null && _activity.IsAllDataRequested)
                {
                    if (_rowsAffected >= 0) _activity.SetTag("db.response.affected_rows", _rowsAffected);
                    if (_returnedRows >= 0) _activity.SetTag("db.response.returned_rows", _returnedRows);
                    _activity.SetTag("db.query.parameters", ParameterSerializer.Serialize(_command.Parameters, _options));
                }

                if (_logger != null && _logger.IsEnabled(_options.LogLevel))
                {
                    long rows = _returnedRows >= 0 ? _returnedRows : _rowsAffected;
                    _logger.Log(
                        _options.LogLevel,
                        _error,
                        "SQL {Operation} ({DurationMs} ms) rows~{Rows}: {Sql} | params: {Parameters}",
                        _operation,
                        _stopwatch.Elapsed.TotalMilliseconds,
                        rows,
                        _options.CaptureCommandText ? _command.CommandText : "(suppressed)",
                        ParameterSerializer.Serialize(_command.Parameters, _options));
                }
            }

            private System.Collections.Generic.KeyValuePair<string, object?> OperationTag()
                => new System.Collections.Generic.KeyValuePair<string, object?>("db.operation.name", _operation);

            public void Dispose() => _activity?.Dispose();
        }
    }
#nullable disable
}
