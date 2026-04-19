using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Marks a column as having a database-side default value.
    /// When <see cref="DefaultValue"/> is provided it is emitted as <c>DEFAULT <i>value</i></c>
    /// in the CREATE TABLE statement; otherwise the column is flagged so that
    /// <c>ExcludeAutoFields()</c> on the insert builder will skip it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class DefaultAttribute : Attribute
    {
        /// <summary>The SQL default expression (e.g. <c>DbDefaults.Time.Now</c>), or <see langword="null"/> for a naked DEFAULT.</summary>
        public string DefaultValue { get; }

        /// <summary>Marks the column as having a DB default without specifying the expression.</summary>
        public DefaultAttribute() { }

        /// <summary>Marks the column with the explicit SQL default expression <paramref name="defaultValue"/>.</summary>
        public DefaultAttribute(string defaultValue) { DefaultValue = defaultValue; }
    }
}
