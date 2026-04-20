using System;
using System.Globalization;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>Emits a <c>CHECK (column &gt;= value)</c> constraint (greater than or equal).</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class BiggerOrEqualAttribute : Attribute
    {
        /// <summary>The bound value as a SQL-ready string.</summary>
        public string Value { get; }
        /// <summary>Greater than or equal to an integer value.</summary>
        public BiggerOrEqualAttribute(long value) { Value = value.ToString(CultureInfo.InvariantCulture); }
        /// <summary>Greater than or equal to a floating-point value.</summary>
        public BiggerOrEqualAttribute(double value) { Value = value.ToString(CultureInfo.InvariantCulture); }
        /// <summary>Greater than or equal to a literal SQL value.</summary>
        public BiggerOrEqualAttribute(string value) { Value = value; }
    }
}
