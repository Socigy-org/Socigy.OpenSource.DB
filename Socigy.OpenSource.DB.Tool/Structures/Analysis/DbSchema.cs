using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Tool.Structures.Analysis
{
    public class DbSchema
    {
        public string Id { get; set; }
        public string? PreviousId { get; set; }

        public IList<DbTable> Tables { get; set; } = [];
    }
}
