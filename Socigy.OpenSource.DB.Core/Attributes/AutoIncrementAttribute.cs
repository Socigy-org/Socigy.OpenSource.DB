using System;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Marks a property as auto-incremented via a database sequence.
    /// The column is excluded from INSERT statements by default (the sequence provides the value).
    /// Use <c>ClassName.PropertySequence.GetNextValueAsync(conn)</c> to retrieve the next value explicitly.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class AutoIncrementAttribute : Attribute
    {
        /// <summary>
        /// Custom sequence name. If null, the name is derived as <c>{table_name}_{column_name}_seq</c>.
        /// </summary>
        public string? SequenceName { get; }

        public AutoIncrementAttribute() { }

        public AutoIncrementAttribute(string sequenceName)
        {
            SequenceName = sequenceName;
        }
    }
}
