using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Socigy.OpenSource.DB.Core.Diagnostics
{
#nullable enable
    /// <summary>
    /// Wraps a <see cref="DbDataReader"/> together with its instrumentation scope so the span spans the
    /// whole result enumeration. Call <see cref="ReadAsync"/> instead of <c>Reader.ReadAsync</c> to count
    /// returned rows; disposing the wrapper closes the span (recording the row count) and the reader.
    /// </summary>
    public sealed class InstrumentedReader : System.IAsyncDisposable, System.IDisposable
    {
        private readonly DbDiagnostics.Scope _scope;
        private long _rows;

        internal InstrumentedReader(DbDataReader reader, DbDiagnostics.Scope scope)
        {
            Reader = reader;
            _scope = scope;
        }

        /// <summary>The underlying reader. Read columns from this; advance with <see cref="ReadAsync"/>.</summary>
        public DbDataReader Reader { get; }

        /// <summary>Advances the reader one row, counting rows for <c>db.response.returned_rows</c>.</summary>
        public async Task<bool> ReadAsync(CancellationToken cancellationToken = default)
        {
            bool more = await Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (more) _rows++;
            return more;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _scope.SetReturnedRows(_rows);
                _scope.Complete();
            }
            finally
            {
                _scope.Dispose();
                // netstandard2.0 DbDataReader has no DisposeAsync; the concrete reader (e.g. NpgsqlDataReader)
                // implements IAsyncDisposable at runtime, so prefer that and fall back to sync Dispose.
                if (Reader is System.IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else
                    Reader.Dispose();
            }
        }

        public void Dispose()
        {
            try
            {
                _scope.SetReturnedRows(_rows);
                _scope.Complete();
            }
            finally
            {
                _scope.Dispose();
                Reader.Dispose();
            }
        }
    }
#nullable disable
}
