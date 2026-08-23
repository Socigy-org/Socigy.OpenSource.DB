using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Tool.Structures.Analysis
{
    public class DbColumn
    {
        public string Name { get; set; }
        public string SourceName { get; set; }

        public string RenamedFrom { get; set; }

        public string DotnetType { get; set; }
        public string DatabaseType { get; set; }

        public bool? Nullable { get; set; }
        public bool? IsPrimaryKey { get; set; }
        /// <summary>Position within a composite primary key (0-based), from <c>[PrimaryKey(order)]</c> or the DB's
        /// key-column ordinal. Null means follow declaration/column order. Orders the emitted <c>PRIMARY KEY (...)</c>
        /// so a composite key whose key order differs from column order round-trips.</summary>
        public int? PrimaryKeyOrder { get; set; }
        public bool? IsUnique { get; set; }

        public string DefaultValue { get; set; }
        public string ValueConvertor { get; set; }

        public bool? IsAutoIncrement { get; set; }
        /// <summary>Sequence name; null means derived as {table}_{column}_seq.</summary>
        public string SequenceName { get; set; }

        /// <summary>Maximum string length from <c>[StringLength]</c>; causes VARCHAR(n) type.</summary>
        public int? MaxLength { get; set; }
        /// <summary>Minimum string length from <c>[StringLength]</c>; emits a CHECK constraint.</summary>
        public int? MinLength { get; set; }

        /// <summary>True when the column is mapped to a <c>jsonb</c> DB type via <c>[RawJsonColumn]</c> or <c>[JsonColumn]</c>.</summary>
        public bool? IsJsonColumn { get; set; }
        /// <summary>Full type name of the <c>JsonSerializerContext</c> subclass; null for raw JSON string columns.</summary>
        public string JsonContextType { get; set; }

        /// <summary>
        /// True when the column carries <c>[Encrypted]</c>. Stored as <c>bytea</c>, so it is otherwise
        /// indistinguishable from a real <c>byte[]</c> — which is precisely why it has to be recorded: the
        /// generator has to refuse the impossible <c>text -&gt; bytea</c> in-place cast when a plaintext column
        /// becomes encrypted, and cannot tell that from the database type alone.
        ///
        /// Null (rather than false) on a column read from a schema snapshot written before this field existed;
        /// see <c>SchemaComparer.DetectColumnChanges</c>, which skips the encryption diff in that case rather
        /// than reporting a change that did not happen.
        /// </summary>
        public bool? IsEncrypted { get; set; }

        /// <summary>
        /// The <c>[Encrypted(Profile = "...")]</c> profile, i.e. which encryptor (and therefore which key and
        /// which code path) the column's data lives under. Null for the default profile.
        ///
        /// Recorded because both sides of a profile change are <c>bytea</c>: without it the change is invisible
        /// to the comparer, no migration is emitted, and every affected row throws on its next typed read — long
        /// after deploy, on the data that is by definition the most sensitive in the schema.
        /// </summary>
        public string EncryptionProfile { get; set; }
    }
}
