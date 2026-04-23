using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Core.Settings
{
    public class SocigySettings
    {
        public DatabaseSettings Database { get; set; }
        public bool ShouldShowMessageOnEmptyMigrationGeneration { get; set; } = true;
    }

    public class DatabaseSettings
    {
        public string MigrationNameTemplate { get; set; } = "${Name}";
        public string Platform { get; set; }
        public bool GenerateDbConnectionFactory { get; set; } = true;
        public bool GenerateWebAppExtensions { get; set; } = true;

#nullable enable
        public string? DatabaseName { get; set; }
#nullable disable
    }
}
