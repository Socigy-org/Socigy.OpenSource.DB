using System;
using System.Globalization;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>Emits a <c>CHECK (column = value)</c> constraint.</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class EqualAttribute : Attribute
    {
        /// <summary>The required value as a SQL-ready string.</summary>
        public string Value { get; }
        /// <summary>Column must equal an integer value.</summary>
        public EqualAttribute(long value) { Value = value.ToString(CultureInfo.InvariantCulture); }
        /// <summary>Column must equal a floating-point value.</summary>
        public EqualAttribute(double value) { Value = value.ToString(CultureInfo.InvariantCulture); }
        /// <summary>Column must equal a literal SQL value.</summary>
        public EqualAttribute(string value) { Value = value; }
    }
}
