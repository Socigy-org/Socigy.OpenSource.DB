using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Attributes
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
    public class TableAttribute(string name) : Attribute
    {
        public string Name { get; } = name;
    }
}
