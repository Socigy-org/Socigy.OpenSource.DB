using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Tool.Structures.Analysis
{
    public class DbColumn
    {
        public string Name { get; set; }
        public string SourceName { get; set; }

        public string RenamedFrom { get; set; }

        public string DotnetType { get; set; }
        public string DatabaseType { get; set; }

        public bool? Nullable { get; set; }
        public bool? IsPrimaryKey { get; set; }
        public bool? IsUnique { get; set; }

        public string DefaultValue { get; set; }
        public string ValueConvertor { get; set; }

        public bool? IsAutoIncrement { get; set; }
        /// <summary>Sequence name; null means derived as {table}_{column}_seq.</summary>
        public string SequenceName { get; set; }

        /// <summary>Maximum string length from <c>[StringLength]</c>; causes VARCHAR(n) type.</summary>
        public int? MaxLength { get; set; }
        /// <summary>Minimum string length from <c>[StringLength]</c>; emits a CHECK constraint.</summary>
        public int? MinLength { get; set; }
    }
}
