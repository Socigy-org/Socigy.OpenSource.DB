using System;
using System.Data.Common;
using System.Threading.Tasks;
using Socigy.OpenSource.DB.Core.Diagnostics;

namespace Socigy.OpenSource.DB.Core
{
#nullable enable
    public abstract class SqlCommandBuilder<T>
        where T : SqlCommandBuilder<T>
    {
        protected DbConnection? _Connection;
        protected DbTransaction? _Transaction;

        /// <summary>
        /// Diagnostics carrier (logger + options sourced from DI) used when this builder is driven by a
        /// generated database context. When <see langword="null"/>, command instrumentation falls back to
        /// the ambient <see cref="SocigyDbDiagnostics"/> configuration.
        /// </summary>
        protected DbDiagnosticsContext? _Diagnostics;

        /// <summary>
        /// Associates a diagnostics context with this builder so executed commands log/trace through the
        /// context's logger and options. Passing <see langword="null"/> is a no-op (ambient config is used).
        /// </summary>
        public T WithDiagnostics(DbDiagnosticsContext? diagnostics)
        {
            _Diagnostics = diagnostics;
            return (T)this;
        }

        /// <summary>
        /// Associates the specified database transaction with the current instance and returns the updated instance.
        /// </summary>
        /// <param name="transaction">The <see cref="DbTransaction"/> to associate with this instance. The transaction must have a valid, open
        /// connection.</param>
        /// <returns>The current instance with the specified transaction associated.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="transaction"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">Thrown if <paramref name="transaction"/> does not have an associated <see cref="DbConnection"/>.</exception>
        public T WithTransaction(DbTransaction transaction)
        {
            _Transaction = transaction;
            if (_Transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            else if (_Transaction.Connection == null)
                throw new ArgumentException("The provided transaction has no associated DbConnection.", nameof(transaction));

            _Connection = _Transaction.Connection;

            return (T)this;
        }

        /// <summary>
        /// Sets the database connection to be used by the current instance and returns the instance for method chaining.
        /// </summary>
        /// <remarks>This method enables fluent configuration by returning the current instance. The provided connection
        /// is not opened or closed by this method.</remarks>
        /// <param name="connection">The database connection to associate with this instance. Cannot be null.</param>
        /// <returns>The current instance with the specified database connection set.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="connection"/> is null.</exception>
        public T WithConnection(DbConnection connection)
        {
            _Connection = connection;
            if (_Connection == null)
                throw new ArgumentNullException(nameof(connection));

            return (T)this;
        }
    }
#nullable disable
}
