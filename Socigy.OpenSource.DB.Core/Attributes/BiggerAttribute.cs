using System;
using System.Globalization;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>Emits a <c>CHECK (column &gt; value)</c> constraint (strictly greater than).</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class BiggerAttribute : Attribute
    {
        /// <summary>The bound value as a SQL-ready string.</summary>
        public string Value { get; }
        /// <summary>Strictly greater than an integer value.</summary>
        public BiggerAttribute(long value) { Value = value.ToString(CultureInfo.InvariantCulture); }
        /// <summary>Strictly greater than a floating-point value.</summary>
        public BiggerAttribute(double value) { Value = value.ToString(CultureInfo.InvariantCulture); }
        /// <summary>Strictly greater than a literal SQL value.</summary>
        public BiggerAttribute(string value) { Value = value; }
    }
}
