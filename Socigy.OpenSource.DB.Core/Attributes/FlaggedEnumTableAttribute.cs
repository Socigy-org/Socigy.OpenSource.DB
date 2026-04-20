using System;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Marks an enum-typed property as a set-of-flags relationship backed by an explicitly
    /// user-defined junction class annotated with <see cref="FlagTableAttribute"/>.
    /// Use this when you need extra columns on the junction table beyond the two FK columns.
    /// </summary>
    /// <example><code>
    /// [FlaggedEnumTable(typeof(UsersUserRole))]
    /// public UserRole Role { get; set; }
    /// </code></example>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class FlaggedEnumTableAttribute : Attribute
    {
        /// <summary>The explicit junction table class (must be annotated with <see cref="FlagTableAttribute"/>).</summary>
        public Type JunctionTableType { get; }

        /// <summary>References the explicit junction table type.</summary>
        public FlaggedEnumTableAttribute(Type junctionTableType) { JunctionTableType = junctionTableType; }
    }
}
