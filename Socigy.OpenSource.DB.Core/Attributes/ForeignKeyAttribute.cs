using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Attributes
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public class ForeignKeyAttribute(Type foreignTable) : Attribute
    {
        public string Name { get; set; }

        /// <summary>
        /// This is the foreign table name that we are referencing
        /// </summary>
        public Type ForeignTable { get; private set; } = foreignTable;

        /// <summary>
        /// Keys in this table. The order should match with <see cref="TargetKeys"/>
        /// </summary>
        public string[] Keys { get; set; }
        /// <summary>
        /// Keys in the target table. The order should match with <see cref="Keys"/>
        /// </summary>
        public string[] TargetKeys { get; set; }

        public string OnDelete { get; set; }
        public string OnUpdate { get; set; }
    }
}
