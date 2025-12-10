using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Attributes
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
    public class RenamedAttribute : Attribute
    {
        public string OldName { get; }

        public RenamedAttribute(string oldName)
        {
            OldName = oldName;
        }
    }
}
