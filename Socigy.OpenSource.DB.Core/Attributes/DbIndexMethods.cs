using System;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Cross-engine sentinel constants for <see cref="IndexAttribute.Method"/>.
    /// <para>
    /// Each constant names what the index is <em>for</em> rather than a specific database's access method, so
    /// a model stays portable: every engine translates the intent to its own equivalent, and reports a warning
    /// when it has none. Reach for <see cref="IndexAttribute.RawMethod"/> only when you need an access method
    /// that has no portable intent.
    /// </para>
    /// </summary>
    public static class DbIndexMethods
    {
        internal const string Prefix = "$socigy$idx$";

        /// <summary>
        /// A general-purpose ordered index, suited to equality, range and sort. The default, and the only
        /// method every engine is guaranteed to support (PostgreSQL: <c>btree</c>).
        /// </summary>
        public const string Default = "$socigy$idx$default";

        /// <summary>
        /// Equality-only lookups. Smaller and faster than <see cref="Default"/> for that one access pattern,
        /// but useless for ranges and sorting (PostgreSQL: <c>hash</c>).
        /// </summary>
        public const string Hash = "$socigy$idx$hash";

        /// <summary>
        /// Text search over the indexed column (PostgreSQL: <c>gin</c>).
        /// </summary>
        public const string FullText = "$socigy$idx$fulltext";

        /// <summary>
        /// Geometric and spatial containment or proximity queries (PostgreSQL: <c>gist</c>).
        /// </summary>
        public const string Spatial = "$socigy$idx$spatial";

        /// <summary>
        /// Containment queries over composite values such as arrays and JSON documents
        /// (PostgreSQL: <c>gin</c>).
        /// </summary>
        public const string Contains = "$socigy$idx$contains";

        /// <summary>
        /// A small summarising index for very large tables whose rows are already physically ordered by the
        /// indexed column, such as an append-only timestamp (PostgreSQL: <c>brin</c>).
        /// </summary>
        public const string BlockRange = "$socigy$idx$block_range";
    }

    /// <summary>
    /// Cross-engine sentinel constants for <see cref="IndexAttribute.Nulls"/>, controlling where NULLs sort
    /// within an index. Ignored, with a warning, by an engine that cannot express NULL ordering.
    /// </summary>
    public static class DbIndexNulls
    {
        internal const string Prefix = "$socigy$idxnulls$";

        /// <summary>NULLs sort before any non-NULL value.</summary>
        public const string First = "$socigy$idxnulls$first";

        /// <summary>NULLs sort after every non-NULL value.</summary>
        public const string Last = "$socigy$idxnulls$last";
    }
}
