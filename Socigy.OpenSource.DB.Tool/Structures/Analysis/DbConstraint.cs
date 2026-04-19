using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace Socigy.OpenSource.DB.Tool.Structures.Analysis
{
    public class DbConstraint
    {
        public static class Types
        {
            public const string Unique = "unique";
            public const string Check = "check";
            public const string ForeignKey = "foreign_key";
        }

        public string Type { get; set; }

        [JsonIgnore]
        private string _Name;
        public string Name
        {
            get
            {
                if (_Name != null)
                    return _Name;

                var prefix = Type switch
                {
                    Types.Unique => "UQ",
                    Types.Check => "CHCK",
                    Types.ForeignKey => "FK",
                    _ => "UNKNW"
                };

                if (Columns != null)
                {
                    StringBuilder builder = new();
                    foreach (var col in Columns)
                        builder.Append($"{col}_");
                    _Name = $"{prefix}_{builder.ToString().TrimEnd('_')}";
                }
                else
                {
                    _Name = $"{prefix}_{Guid.NewGuid():N}";
                }

                return _Name;
            }

            set { _Name = value; }
        }

        public IEnumerable<string> Columns { get; set; }

        public string Value { get; set; }

        // Foreign keys
        /// <summary>
        /// Target table that has the primary key
        /// </summary>
        public string TargetTable { get; set; }
        /// <summary>
        /// The primary keys that match ours <see cref="Columns"/>
        /// </summary>
        public IEnumerable<string> TargetColumns { get; set; }

        /// <summary>
        /// Gets or sets the action to perform when a related entity is deleted.
        /// </summary>
        /// <remarks>The value typically specifies the referential action, such as "Cascade", "SetNull",
        /// or "Restrict". The supported values and their effects may depend on the underlying data store or
        /// framework.</remarks>
        // TODO: Make framework for better globalization between databases so that it can be transfered to other engines as well easily
        public string OnDelete { get; set; }
        /// <summary>
        /// Gets or sets the SQL expression to use for updating the column value when a row is modified.
        /// </summary>
        /// <remarks>This property is typically used to specify a database-generated value or function,
        /// such as a timestamp or computed value, that should be applied automatically during update operations. The
        /// exact syntax and supported expressions depend on the underlying database provider.</remarks>
        // TODO: Make framework for better globalization between databases so that it can be transfered to other engines as well easily
        public string OnUpdate { get; set; }
    }
}
