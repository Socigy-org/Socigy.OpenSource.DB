using System;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Marks an enum-typed property as a set-of-flags relationship backed by a junction (N:M) table.
    /// The junction table is auto-generated as <c>{mainTable}_{enumTable}</c> unless overridden via
    /// <see cref="TableName"/>.
    /// <para>
    /// Column names in the junction table are derived automatically:
    /// each primary-key column of the owning table maps to <c>{mainTable}_{pkCol}</c>,
    /// and the enum column maps to <c>{enumTable}_id</c>.
    /// You can override individual mappings by passing alternating
    /// <c>(localPropertyName, junctionColumnName)</c> pairs in <paramref name="keyMappings"/>.
    /// </para>
    /// </summary>
    /// <example><code>
    /// // Auto-derive all names:
    /// [FlaggedEnum]
    /// public UserRole Role { get; set; }
    ///
    /// // Override junction column names (alternating pairs):
    /// [FlaggedEnum(nameof(Id), "user_id")]
    /// public UserRole Role { get; set; }
    /// </code></example>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class FlaggedEnumAttribute : Attribute
    {
        /// <summary>Optional custom name for the auto-generated junction table.</summary>
        public string? TableName { get; set; }

        /// <summary>
        /// Alternating <c>(localPropertyName, junctionColumnName)</c> pairs that override
        /// the auto-derived column names in the junction table.
        /// </summary>
        public string[] KeyMappings { get; }

        /// <summary>Creates a <see cref="FlaggedEnumAttribute"/> with fully auto-derived names.</summary>
        public FlaggedEnumAttribute() { KeyMappings = []; }

        /// <summary>
        /// Creates a <see cref="FlaggedEnumAttribute"/> with explicit column name mappings.
        /// Pass alternating <c>localPropertyName, junctionColumnName</c> pairs.
        /// </summary>
        public FlaggedEnumAttribute(params string[] keyMappings) { KeyMappings = keyMappings; }
    }
}
