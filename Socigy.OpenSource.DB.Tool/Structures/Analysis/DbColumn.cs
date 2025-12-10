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
    }
}
