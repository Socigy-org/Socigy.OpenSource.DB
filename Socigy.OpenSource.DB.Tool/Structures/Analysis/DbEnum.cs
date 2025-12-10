using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Tool.Structures.Analysis
{
    internal class DbEnum
    {
        public string Name { get; set; }
        public string SourceName { get; set; }

        public Dictionary<string, int> Mappings { get; set; }
    }
}
