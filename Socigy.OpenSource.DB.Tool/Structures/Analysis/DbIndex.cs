using System;
using System.Collections.Generic;
using System.Linq;

namespace Socigy.OpenSource.DB.Tool.Structures.Analysis
{
    /// <summary>
    /// An index declared by <c>[Index]</c>, in engine-neutral terms.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT modelled as a <see cref="DbConstraint"/>: an index is a statement of its own rather
    /// than part of the CREATE TABLE body, and it carries options (access method, partial predicate, covering
    /// columns, per-column ordering) a table constraint has no room for.
    /// <para>
    /// Everything here except <see cref="Where"/> and <see cref="RawMethod"/> describes intent, not SQL, so
    /// this type and the schema snapshot it is serialised into stay shared across database engines. Each
    /// generator renders it, and <c>IndexPlanner</c> resolves anything the engine cannot express.
    /// </para>
    /// </remarks>
    public class DbIndex
    {
        /// <summary>
        /// Explicit index name from <c>[Index(Name = "...")]</c>, or null when it should be derived. The
        /// derivation needs the engine's identifier limit, so it lives in <c>IndexPlanner</c> rather than in
        /// a computed property here.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The SQL table this index belongs to. Always populated: an engine whose DROP INDEX requires the
        /// owning table (MySQL, SQL Server) has no other way to recover it.
        /// </summary>
        public string TableName { get; set; }

        /// <summary>
        /// Key columns in index order, as C# property names. Resolved to database column names at generation
        /// time, the same way <see cref="DbConstraint.Columns"/> is.
        /// </summary>
        public IEnumerable<string> Columns { get; set; }

        /// <summary>Whether the index enforces uniqueness across <see cref="Columns"/>.</summary>
        public bool IsUnique { get; set; }

        /// <summary>
        /// A <c>DbIndexMethods</c> intent token, or null for the default ordered index. Translated per engine.
        /// </summary>
        public string Method { get; set; }

        /// <summary>
        /// Engine-specific access method that overrides <see cref="Method"/>, passed through verbatim.
        /// Set only by an explicit <c>[Index(RawMethod = "...")]</c> or recovered by scaffolding from an
        /// access method with no portable intent.
        /// </summary>
        public string RawMethod { get; set; }

        /// <summary>Raw SQL predicate for a partial index, passed through verbatim, or null.</summary>
        public string Where { get; set; }

        /// <summary>Non-key covering columns (property names), or null.</summary>
        public IEnumerable<string> IncludeColumns { get; set; }

        /// <summary>Key columns that sort descending (property names). Absence means ascending.</summary>
        public IEnumerable<string> DescendingColumns { get; set; }

        /// <summary>Key columns that sort NULLs first (property names).</summary>
        public IEnumerable<string> NullsFirstColumns { get; set; }

        /// <summary>Key columns that sort NULLs last (property names).</summary>
        public IEnumerable<string> NullsLastColumns { get; set; }

        /// <summary>True when the index specifies any non-default ordering.</summary>
        public bool HasOrdering =>
            (DescendingColumns?.Any() ?? false) ||
            (NullsFirstColumns?.Any() ?? false) ||
            (NullsLastColumns?.Any() ?? false);

        /// <summary>
        /// Whether two indexes describe the same thing. Used by the schema comparer: an index has no in-place
        /// ALTER on any engine, so a difference here means drop and recreate.
        /// </summary>
        /// <remarks>
        /// Key columns are order-sensitive (an index on (a, b) does not serve the same queries as one on
        /// (b, a)); the option sets are not.
        /// </remarks>
        public static bool AreFunctionallyEqual(DbIndex a, DbIndex b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;

            if (a.IsUnique != b.IsUnique) return false;
            if (!SameToken(a.Method, b.Method)) return false;
            if (!SameToken(a.RawMethod, b.RawMethod)) return false;
            if (!SameToken(a.Where, b.Where)) return false;

            // An explicitly named index is only the same index as one with the same explicit name; a derived
            // name is compared through the options it is derived from.
            if (!string.IsNullOrEmpty(a.Name) || !string.IsNullOrEmpty(b.Name))
                if (!SameToken(a.Name, b.Name)) return false;

            if (!SameOrderedColumns(a.Columns, b.Columns)) return false;

            return SameColumnSet(a.IncludeColumns, b.IncludeColumns)
                && SameColumnSet(a.DescendingColumns, b.DescendingColumns)
                && SameColumnSet(a.NullsFirstColumns, b.NullsFirstColumns)
                && SameColumnSet(a.NullsLastColumns, b.NullsLastColumns);
        }

        private static bool SameToken(string a, string b) =>
            string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);

        private static bool SameOrderedColumns(IEnumerable<string> a, IEnumerable<string> b) =>
            (a ?? []).SequenceEqual(b ?? [], StringComparer.OrdinalIgnoreCase);

        private static bool SameColumnSet(IEnumerable<string> a, IEnumerable<string> b)
        {
            var left = new HashSet<string>(a ?? [], StringComparer.OrdinalIgnoreCase);
            var right = new HashSet<string>(b ?? [], StringComparer.OrdinalIgnoreCase);
            return left.SetEquals(right);
        }
    }
}
