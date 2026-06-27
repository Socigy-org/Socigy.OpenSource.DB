using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>Marks a property as (part of) the table's primary key.</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class PrimaryKeyAttribute : Attribute
    {
        /// <summary>
        /// Position of this column within a COMPOSITE primary key (0-based). Only meaningful when the key spans more
        /// than one column AND its key order differs from the property declaration order. Defaults to 0, preserving
        /// the existing behavior (composite key order follows property declaration order). Database-first
        /// scaffolding sets this so a composite PK whose key order differs from column order round-trips.
        /// </summary>
        public int Order { get; }

        public PrimaryKeyAttribute(int order = 0) => Order = order;
    }
}
