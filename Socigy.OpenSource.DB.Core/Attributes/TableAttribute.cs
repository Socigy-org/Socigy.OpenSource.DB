using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Maps a class or enum to a database table with the given SQL name.
    /// Classes annotated with this attribute are picked up by the source generator and migration tool.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
    public class TableAttribute(string name) : Attribute
    {
        /// <summary>The SQL table name.</summary>
        public string Name { get; } = name;
    }
}
