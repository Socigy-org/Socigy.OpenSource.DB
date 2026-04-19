using System;
using System.Globalization;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>Emits a <c>CHECK (column &lt; value)</c> constraint (strictly less than).</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class LowerAttribute : Attribute
    {
        /// <summary>The bound value as a SQL-ready string.</summary>
        public string Value { get; }
        /// <summary>Strictly less than an integer value.</summary>
        public LowerAttribute(long value) { Value = value.ToString(CultureInfo.InvariantCulture); }
        /// <summary>Strictly less than a floating-point value.</summary>
        public LowerAttribute(double value) { Value = value.ToString(CultureInfo.InvariantCulture); }
        /// <summary>Strictly less than a literal SQL value.</summary>
        public LowerAttribute(string value) { Value = value; }
    }
}
