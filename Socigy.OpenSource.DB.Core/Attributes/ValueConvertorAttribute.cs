using System;

namespace Socigy.OpenSource.DB.Attributes
{
#nullable enable
    /// <summary>
    /// Marks a property as using a custom <see cref="Socigy.OpenSource.DB.Core.Convertors.IDbValueConvertor{T}"/>
    /// when reading from or writing to the database.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class ValueConvertorAttribute : Attribute
    {
        /// <summary>The <see cref="Socigy.OpenSource.DB.Core.Convertors.IDbValueConvertor{T}"/> type to use.</summary>
        public Type ConvertorType { get; }

        public ValueConvertorAttribute(Type convertorType)
        {
            ConvertorType = convertorType;
        }
    }
#nullable disable
}
