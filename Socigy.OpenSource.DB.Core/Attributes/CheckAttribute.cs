using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Attributes
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
    public class CheckAttribute(string statement) : Attribute
    {
        public string Statement { get; } = statement;
        public string Name { get; set; }
    }
}
