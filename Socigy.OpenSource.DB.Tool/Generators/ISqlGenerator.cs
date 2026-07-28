using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Tool.Generators
{
    /// <summary>
    /// Index features a database engine can express. An engine declares what it supports and
    /// <see cref="IndexPlanner"/> resolves anything else, so the degradation rules are written once instead
    /// of once per engine.
    /// </summary>
    // Public because the generators that declare it are public types implementing this internal interface.
    [Flags]
    public enum IndexCapabilities
    {
        None = 0,

        /// <summary>CREATE UNIQUE INDEX.</summary>
        Unique = 1 << 0,
        /// <summary>A predicate restricting the index to matching rows (a partial / filtered index).</summary>
        Partial = 1 << 1,
        /// <summary>Non-key covering columns (INCLUDE).</summary>
        Include = 1 << 2,
        /// <summary>Per-column descending sort order.</summary>
        Descending = 1 << 3,
        /// <summary>Per-column NULLS FIRST / NULLS LAST ordering.</summary>
        NullsOrdering = 1 << 4,

        /// <summary>An equality-only hash index.</summary>
        Hash = 1 << 5,
        /// <summary>A text-search index.</summary>
        FullText = 1 << 6,
        /// <summary>A spatial index.</summary>
        Spatial = 1 << 7,
        /// <summary>A containment index over arrays / documents.</summary>
        Contains = 1 << 8,
        /// <summary>A block-range summarising index.</summary>
        BlockRange = 1 << 9,

        All = Unique | Partial | Include | Descending | NullsOrdering
            | Hash | FullText | Spatial | Contains | BlockRange,
    }

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

        /// <summary>Index features this engine can express.</summary>
        IndexCapabilities IndexSupport { get; }

        /// <summary>
        /// Longest identifier the engine accepts, in bytes (PostgreSQL 63, MySQL 64, SQL Server 128). Engines
        /// silently truncate past this, so generated names are shortened deterministically instead.
        /// </summary>
        int MaxIdentifierLength { get; }
    }
}
