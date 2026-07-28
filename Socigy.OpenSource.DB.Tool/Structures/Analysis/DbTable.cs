using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Tool.Structures.Analysis
{
    public class DbTable
    {
        /// <summary>
        /// Name of the SQL Table
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Name of the source class from which we generated the SQL Table
        /// </summary>
        public string SourceName { get; set; }
        public string RenamedFrom { get; set; }

        public bool? IsEnum { get; set; }
        public bool? IsBitfield { get; set; }
        /// <summary>True for tables generated from <c>[FlagTable]</c> or auto-generated junction tables.</summary>
        public bool? IsFlagTable { get; set; }
        public IList<Dictionary<string, object?>>? InstantiatedValues { get; set; }

        public IList<DbColumn> Columns { get; set; }
        public IList<DbConstraint> Constraints { get; set; }

        /// <summary>
        /// Indexes declared by <c>[Index]</c>. Null in a snapshot written before index support existed, which
        /// reads as "no indexes" and makes the next migration create the declared ones.
        /// </summary>
        public IList<DbIndex> Indexes { get; set; }
    }
}
