using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Socigy.OpenSource.DB.Core.Bulk
{
#nullable enable
    /// <summary>
    /// One column in a binary COPY: its already-quoted DB name, the CLR type (so the provider bridge can
    /// infer the wire type), the JSON/encrypted overrides, and a closure reading the wire-ready value off a
    /// boxed row. The value returned by <see cref="GetValue"/> is exactly what the parameterized insert path
    /// would bind — encrypted columns yield ciphertext <c>byte[]</c>, JSON columns yield serialized text,
    /// value convertors are already applied — so the COPY path never re-runs those transforms.
    /// </summary>
    public sealed class CopyColumn
    {
        public string QuotedName { get; }
        public Type ClrType { get; }
        public bool IsJson { get; }
        public bool IsEncrypted { get; }
        public Func<object, object?> GetValue { get; }

        public CopyColumn(string quotedName, Type clrType, bool isJson, bool isEncrypted, Func<object, object?> getValue)
        {
            QuotedName = quotedName;
            ClrType = clrType;
            IsJson = isJson;
            IsEncrypted = isEncrypted;
            GetValue = getValue;
        }
    }

    /// <summary>
    /// Performs a PostgreSQL binary COPY of <paramref name="rows"/> using <paramref name="copyCommand"/>
    /// (a complete <c>COPY … FROM STDIN (FORMAT BINARY)</c> statement) and the ordered
    /// <paramref name="columns"/>. Returns the number of rows written. Implemented in generated code that
    /// references Npgsql, so Core itself stays provider-agnostic.
    /// </summary>
    public delegate Task<ulong> BinaryCopyHandler(
        DbConnection connection,
        DbTransaction? transaction,
        string copyCommand,
        CopyColumn[] columns,
        IReadOnlyList<object> rows,
        CancellationToken cancellationToken);

    /// <summary>
    /// Bridges the Npgsql-agnostic Core to the provider-specific binary-COPY path. The generated Socigy DB
    /// assembly registers the real <see cref="BinaryCopyHandler"/> (built on Npgsql's binary importer) via a
    /// module initializer at load time, so <see cref="BulkCopy"/> and
    /// <c>DynamicTable&lt;T&gt;.InsertMultipleCopyAsync</c> work without Core ever referencing Npgsql.
    /// </summary>
    public static class BulkCopySupport
    {
        private static BinaryCopyHandler? _handler;

        /// <summary>True once a generated assembly has registered the COPY bridge.</summary>
        public static bool IsAvailable => _handler != null;

        /// <summary>Registers the binary-COPY implementation. Called by generated code at module load; the
        /// last registration wins (every generated bridge installs the same Npgsql implementation).</summary>
        public static void Register(BinaryCopyHandler handler)
            => _handler = handler ?? throw new ArgumentNullException(nameof(handler));

        /// <summary>Invokes the registered COPY handler, or throws a clear error if none is registered.</summary>
        public static Task<ulong> CopyAsync(
            DbConnection connection,
            DbTransaction? transaction,
            string copyCommand,
            CopyColumn[] columns,
            IReadOnlyList<object> rows,
            CancellationToken cancellationToken)
        {
            BinaryCopyHandler handler = _handler ?? throw new InvalidOperationException(
                "Binary COPY support is not registered. Ensure your generated Socigy DB assembly is loaded; " +
                "it installs the Npgsql COPY bridge at module load.");
            return handler(connection, transaction, copyCommand, columns, rows, cancellationToken);
        }
    }
#nullable disable
}
