using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Tool.Structures
{
    public class SocigySettings
    {
        public required DatabaseSettings Database { get; set; }
    }

    public class DatabaseSettings
    {
        public string MigrationNameTemplate { get; set; } = "${D}_${Name}";
        public required string Platform { get; set; }
    }
}
