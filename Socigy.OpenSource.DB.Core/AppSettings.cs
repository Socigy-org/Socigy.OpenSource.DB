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

        /// <summary>
        /// Optional base name for the generated C# identifiers (the <c>I{X}</c> context interface, <c>Add{X}()</c>
        /// DI methods, <c>{X}Factory</c>, namespaces). When unset, a valid identifier is derived from
        /// <see cref="DatabaseName"/>. Lets a lowercase, Postgres-conventional <c>databaseName</c> (e.g.
        /// <c>identity</c>) keep producing clean identifiers (e.g. <c>IIdentityDb</c>) while the physical
        /// database / connection-string key / DI service key stay <see cref="DatabaseName"/>.
        /// </summary>
        public string? ContextName { get; set; }
#nullable disable
    }
}
