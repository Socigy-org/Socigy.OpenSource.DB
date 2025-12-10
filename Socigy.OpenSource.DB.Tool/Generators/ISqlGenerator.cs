using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Tool.Generators
{
    internal interface ISqlGenerator
    {
        /// <summary>
        /// Generates a list of SQL commands to apply the schema differences.
        /// </summary>
        /// <param name="diff">The calculated difference between schemas.</param>
        /// <returns>(upSQL[], downSql[])</returns>
        (IEnumerable<string> Up, IEnumerable<string> Down) Generate(SchemaDiff diff);

        string GetDatabaseType(string csharpType);
    }
}
