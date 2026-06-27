using System;
using System.Collections.Generic;

namespace Socigy.OpenSource.DB.Core.Encryption.Reencryption
{
#nullable enable
    /// <summary>Tuning knobs for a <see cref="FieldReencryptor"/> pass.</summary>
    public sealed class ReencryptOptions
    {
        /// <summary>How many rows to read and update per batch/transaction (default 500).</summary>
        public int BatchSize { get; set; } = 500;

        /// <summary>
        /// When <see langword="true"/>, scan and count what <i>would</i> be upgraded without writing anything.
        /// </summary>
        public bool DryRun { get; set; }

        /// <summary>
        /// Force re-encryption of every encrypted value, even ones the encryptor reports as already current.
        /// Required to re-encrypt values whose encryptor has no version concept (e.g. a plain single-key
        /// encryptor); otherwise such values are left untouched.
        /// </summary>
        /// <remarks>
        /// A pass commits per batch and is not resumable, so if it dies partway the table is left with some
        /// rows on the old key and some on the new. With a versioned encryptor (<c>KeyringFieldEncryptor</c> or
        /// Vault Transit) both keys still decrypt, so a re-run finishes the job safely. With a plain single-key
        /// <c>AesFieldEncryptor</c> whose key you are swapping, keep BOTH the old and new keys loaded (run the
        /// pass through a keyring that holds both) until it completes, or a partial pass becomes undecryptable.
        /// </remarks>
        public bool Force { get; set; }

        /// <summary>Optional per-batch progress callback (table name, rows scanned so far, cells upgraded so far).</summary>
        public Action<string, long, long>? OnProgress { get; set; }
    }

    /// <summary>The outcome of a <see cref="FieldReencryptor"/> pass.</summary>
    public sealed class ReencryptReport
    {
        /// <summary>Per-table counters, keyed by the SQL table name that was scanned.</summary>
        public Dictionary<string, ReencryptTableResult> Tables { get; } =
            new Dictionary<string, ReencryptTableResult>(StringComparer.Ordinal);

        /// <summary>Total rows scanned across all tables.</summary>
        public long TotalRowsScanned { get; set; }

        /// <summary>Total encrypted cells rewritten to the current key across all tables.</summary>
        public long TotalCellsUpgraded { get; set; }
    }

    /// <summary>Per-table counters within a <see cref="ReencryptReport"/>.</summary>
    public sealed class ReencryptTableResult
    {
        public long RowsScanned { get; set; }
        public long CellsUpgraded { get; set; }
    }
#nullable disable
}
