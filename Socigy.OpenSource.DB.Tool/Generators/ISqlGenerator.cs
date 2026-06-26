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
        (IEnumerable<string> Up, IEnumerable<string> Down) Generate(SchemaDiff diff, bool isFirstMigration);

        /// <summary>Human-readable data-losing operations produced by the most recent <see cref="Generate"/> call.</summary>
        IReadOnlyList<string> DestructiveOperations { get; }

        /// <summary>
        /// Non-destructive but risky operations from the most recent <see cref="Generate"/> call that a
        /// developer should review (e.g. a NOT NULL column added without a default, or a drop+add that looks
        /// like an unmarked rename). Distinct from <see cref="DestructiveOperations"/>: these may fail at apply
        /// time or silently lose data, but the generated SQL itself is not unconditionally destructive.
        /// </summary>
        IReadOnlyList<string> SafetyWarnings { get; }

        string GetDatabaseType(string csharpType);
    }
}
