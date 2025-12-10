using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Attributes
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class DefaultAttribute : Attribute
    {
        public string DefaultValue { get; }

        public DefaultAttribute() { }
        public DefaultAttribute(string defaultValue)
        {
            DefaultValue = defaultValue;
        }
    }
}
