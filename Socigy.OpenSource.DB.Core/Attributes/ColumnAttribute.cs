using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Attributes
{
#nullable enable
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class ColumnAttribute : Attribute
    {
        public string? Name { get; private set; }

        public string? Type { get; set; }
        public Type? ValueConvertor { get; set; }

        public ColumnAttribute() { }
        public ColumnAttribute(string name)
        {
            Name = name;
        }
    }
#nullable disable
}
