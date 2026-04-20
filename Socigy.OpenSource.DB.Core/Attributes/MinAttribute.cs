using System;
using System.Globalization;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Emits a <c>CHECK (column &gt;= value)</c> constraint on the column.
    /// Applicable to numeric and date columns.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class MinAttribute : Attribute
    {
        /// <summary>The minimum value as a SQL-ready string.</summary>
        public string Value { get; }

        /// <summary>Sets the minimum to an integer value.</summary>
        public MinAttribute(long value) { Value = value.ToString(CultureInfo.InvariantCulture); }

        /// <summary>Sets the minimum to a floating-point value.</summary>
        public MinAttribute(double value) { Value = value.ToString(CultureInfo.InvariantCulture); }

        /// <summary>Sets the minimum to a literal SQL value (e.g. a date string <c>'2020-01-01'</c>).</summary>
        public MinAttribute(string value) { Value = value; }
    }
}
