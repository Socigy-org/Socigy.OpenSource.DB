using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Attributes
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public class UniqueAttribute : Attribute
    {
        public string Name { get; set; }

        public IEnumerable<string> Columns { get; private set; }

        /// <summary>
        /// This constructor should be used on a property
        /// </summary>
        public UniqueAttribute()
        {
            Columns = [];
        }


        /// <summary>
        /// This should be used on the class definition, because you reference multiple properties
        /// </summary>
        public UniqueAttribute(params string[] columns)
        {
            Columns = columns;
        }
    }
}
