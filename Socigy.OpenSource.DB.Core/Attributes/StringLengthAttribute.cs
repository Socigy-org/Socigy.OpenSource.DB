using System;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Constrains the maximum (and optionally minimum) length of a string column.
    /// Causes the column to use <c>VARCHAR(MaxLength)</c> instead of <c>TEXT</c>,
    /// and emits a <c>CHECK (LENGTH(col) &gt;= MinLength)</c> constraint when <see cref="MinLength"/> &gt; 0.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class StringLengthAttribute : Attribute
    {
        /// <summary>Maximum allowed length; maps to <c>VARCHAR(MaxLength)</c>.</summary>
        public int MaxLength { get; }

        /// <summary>Minimum required length; emits a <c>CHECK</c> constraint when &gt; 0.</summary>
        public int MinLength { get; }

        /// <summary>Constrains the column to at most <paramref name="maxLength"/> characters.</summary>
        public StringLengthAttribute(int maxLength) { MaxLength = maxLength; }

        /// <summary>Constrains the column between <paramref name="minLength"/> and <paramref name="maxLength"/> characters.</summary>
        public StringLengthAttribute(int minLength, int maxLength) { MinLength = minLength; MaxLength = maxLength; }
    }
}
