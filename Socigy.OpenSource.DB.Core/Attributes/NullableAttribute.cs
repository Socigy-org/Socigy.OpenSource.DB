using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Core.Attributes
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class NullableAttribute : Attribute
    {
        public NullableAttribute()
        {
        }
    }
}
