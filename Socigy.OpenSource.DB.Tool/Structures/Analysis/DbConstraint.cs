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

        /// <summary>The SQL table name this constraint belongs to. Used to disambiguate auto-generated names.</summary>
        public string TableName { get; set; }

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

                var tablePrefix = !string.IsNullOrEmpty(TableName) ? $"{TableName}_" : "";

                if (Columns != null)
                {
                    StringBuilder builder = new();
                    foreach (var col in Columns)
                        builder.Append($"{col}_");
                    _Name = $"{prefix}_{tablePrefix}{builder.ToString().TrimEnd('_')}";
                }
                else
                {
                    // No column list (e.g. a raw-expression CHECK): derive a STABLE suffix from the
                    // constraint's content. A random GUID here would change every regeneration, making
                    // migrations non-reproducible and the DOWN-script's DROP unable to match a name from
                    // a previously generated UP that was produced by a different process run.
                    var basis = $"{Type}|{TableName}|{Value}|{TargetTable}|" +
                                $"{(TargetColumns != null ? string.Join(",", TargetColumns) : "")}";
                    _Name = $"{prefix}_{tablePrefix}{StableHash(basis)}";
                }

                return _Name;
            }

            set { _Name = value; }
        }

        // Deterministic FNV-1a 32-bit hash — unlike string.GetHashCode() it is stable across runs and
        // processes, so generated constraint names are reproducible.
        private static string StableHash(string s)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in s) { hash ^= c; hash *= 16777619; }
                return hash.ToString("x8");
            }
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
