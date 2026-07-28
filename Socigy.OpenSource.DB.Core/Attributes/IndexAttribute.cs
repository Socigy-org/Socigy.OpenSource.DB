using System;
using System.Collections.Generic;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Declares a database index over one or more columns.
    /// <para>
    /// Use the parameterless form on a property for a single-column index, and the
    /// <c>[Index(nameof(A), nameof(B))]</c> form on the class for a composite one, exactly as
    /// <see cref="UniqueAttribute"/> works. The attribute may be applied more than once: a table has many
    /// indexes, and a single column can carry both a plain and a partial index.
    /// </para>
    /// <para>
    /// Every option except <see cref="Where"/> and <see cref="RawMethod"/> describes the index in
    /// engine-neutral terms; the target database engine decides how to express it. An option the engine cannot
    /// express is dropped with a warning when it only affects performance, and is reported as an error when
    /// dropping it would change what the index means.
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// [Table("users")]
    /// [Index(nameof(TenantId), nameof(Email), Unique = true)]
    /// public partial class User
    /// {
    ///     [Index] public string Email { get; set; }
    ///     [Index(Method = DbIndexMethods.FullText)] public string Bio { get; set; }
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public class IndexAttribute : Attribute
    {
        /// <summary>
        /// Indexes the property this attribute is applied to. Use on a property.
        /// </summary>
        public IndexAttribute()
        {
            Columns = [];
        }

        /// <summary>
        /// Indexes the listed columns, in the given order. Use on the class definition, because it
        /// references multiple properties (typically via <c>nameof</c>).
        /// </summary>
        public IndexAttribute(params string[] columns)
        {
            Columns = columns;
        }

        /// <summary>
        /// Key columns, in index order. Empty for the property-level form, where the column is the property
        /// the attribute is applied to.
        /// </summary>
        public IEnumerable<string> Columns { get; private set; }

        /// <summary>
        /// Index name. Left unset, a deterministic name is derived from the table, the key columns and any
        /// option that distinguishes this index from another over the same columns.
        /// </summary>
        public string Name { get; set; }

        /// <summary>Enforces uniqueness across the key columns.</summary>
        public bool Unique { get; set; }

        /// <summary>
        /// What the index is for, as one of the <see cref="DbIndexMethods"/> constants. Each engine maps the
        /// intent to its own access method. Defaults to <see cref="DbIndexMethods.Default"/>.
        /// </summary>
        public string Method { get; set; }

        /// <summary>
        /// Non-key columns stored in the index so a query reading only these can be answered from the index
        /// alone. Property names. Purely a performance option: an engine without covering indexes ignores it.
        /// </summary>
        public string[] Include { get; set; }

        /// <summary>
        /// Sorts every key column in descending order. Use <see cref="DescendingColumns"/> instead when only
        /// some of the columns are descending.
        /// </summary>
        public bool Descending { get; set; }

        /// <summary>
        /// Where NULLs sort for every key column, as one of the <see cref="DbIndexNulls"/> constants. Use
        /// <see cref="NullsFirstColumns"/> / <see cref="NullsLastColumns"/> when the columns differ.
        /// </summary>
        public string Nulls { get; set; }

        /// <summary>
        /// The key columns that sort descending, by property name. Overrides <see cref="Descending"/> for the
        /// columns it names.
        /// </summary>
        public string[] DescendingColumns { get; set; }

        /// <summary>
        /// The key columns that sort NULLs first, by property name. Overrides <see cref="Nulls"/> for the
        /// columns it names.
        /// </summary>
        public string[] NullsFirstColumns { get; set; }

        /// <summary>
        /// The key columns that sort NULLs last, by property name. Overrides <see cref="Nulls"/> for the
        /// columns it names.
        /// </summary>
        public string[] NullsLastColumns { get; set; }

        /// <summary>
        /// Raw SQL predicate restricting the index to the rows that match it (a partial index).
        /// <para>
        /// ESCAPE HATCH: the expression is passed to the database verbatim, so a model using it is tied to one
        /// database engine. On an engine without partial indexes the predicate is dropped with a warning, or,
        /// when <see cref="Unique"/> is also set, reported as an error, because indexing the full table would
        /// enforce uniqueness over rows the predicate deliberately excluded.
        /// </para>
        /// </summary>
        public string Where { get; set; }

        /// <summary>
        /// The engine's own access method name (for PostgreSQL, e.g. <c>"gist"</c>), overriding
        /// <see cref="Method"/>.
        /// <para>
        /// ESCAPE HATCH: the value is passed to the database verbatim, so a model using it is tied to one
        /// database engine. Prefer <see cref="Method"/>, which every engine can translate.
        /// </para>
        /// </summary>
        public string RawMethod { get; set; }
    }
}
