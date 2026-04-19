using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Attributes
{
#nullable enable
    /// <summary>
    /// Customises how a property maps to a database column.
    /// Override the column name, SQL type, or supply a custom <see cref="ValueConvertor"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class ColumnAttribute : Attribute
    {
        /// <summary>Optional explicit SQL column name; defaults to the snake_case property name.</summary>
        public string? Name { get; private set; }

        /// <summary>Optional explicit SQL column type (e.g. <c>"JSONB"</c>).</summary>
        public string? Type { get; set; }

        /// <summary>Optional <see cref="IDbValueConvertor{T}"/> type used to convert values to/from the DB.</summary>
        public Type? ValueConvertor { get; set; }

        /// <summary>Uses the property name (converted to snake_case) as the column name.</summary>
        public ColumnAttribute() { }

        /// <summary>Uses <paramref name="name"/> as the column's SQL name.</summary>
        public ColumnAttribute(string name) { Name = name; }
    }
#nullable disable
}
