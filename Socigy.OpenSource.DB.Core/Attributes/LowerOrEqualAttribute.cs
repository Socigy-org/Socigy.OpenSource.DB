using System;
using System.Globalization;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>Emits a <c>CHECK (column &lt;= value)</c> constraint (less than or equal).</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class LowerOrEqualAttribute : Attribute
    {
        /// <summary>The bound value as a SQL-ready string.</summary>
        public string Value { get; }
        /// <summary>Less than or equal to an integer value.</summary>
        public LowerOrEqualAttribute(long value) { Value = value.ToString(CultureInfo.InvariantCulture); }
        /// <summary>Less than or equal to a floating-point value.</summary>
        public LowerOrEqualAttribute(double value) { Value = value.ToString(CultureInfo.InvariantCulture); }
        /// <summary>Less than or equal to a literal SQL value.</summary>
        public LowerOrEqualAttribute(string value) { Value = value; }
    }
}
