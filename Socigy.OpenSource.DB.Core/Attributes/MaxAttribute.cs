using System;
using System.Globalization;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Emits a <c>CHECK (column &lt;= value)</c> constraint on the column.
    /// Applicable to numeric and date columns.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class MaxAttribute : Attribute
    {
        /// <summary>The maximum value as a SQL-ready string.</summary>
        public string Value { get; }

        /// <summary>Sets the maximum to an integer value.</summary>
        public MaxAttribute(long value) { Value = value.ToString(CultureInfo.InvariantCulture); }

        /// <summary>Sets the maximum to a floating-point value.</summary>
        public MaxAttribute(double value) { Value = value.ToString(CultureInfo.InvariantCulture); }

        /// <summary>Sets the maximum to a literal SQL value (e.g. a date string <c>'2020-01-01'</c>).</summary>
        public MaxAttribute(string value) { Value = value; }
    }
}
