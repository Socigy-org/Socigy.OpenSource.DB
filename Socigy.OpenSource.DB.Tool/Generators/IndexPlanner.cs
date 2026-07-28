using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Socigy.OpenSource.DB.Tool.Generators
{
    /// <summary>
    /// Turns an engine-neutral <see cref="DbIndex"/> into something a specific engine can actually render:
    /// resolves the key columns to database column names, derives a stable index name within the engine's
    /// identifier limit, and drops or rejects options the engine cannot express.
    /// </summary>
    /// <remarks>
    /// This is the one place the "what if the engine cannot do that" rules live, so a new engine inherits
    /// them by declaring its <see cref="IndexCapabilities"/> rather than reimplementing the policy.
    /// <para>
    /// The split is between options that only change performance and options that change meaning. A covering
    /// column list, an access method or a sort order can be dropped: the index still returns the same rows,
    /// just less efficiently, so it degrades with a warning. Uniqueness and a partial predicate on a unique
    /// index change which rows the database will accept, so they are refused outright rather than silently
    /// altering what the schema enforces.
    /// </para>
    /// The planner never quotes identifiers or emits SQL; quoting differs per engine and stays with the
    /// generator.
    /// </remarks>
    internal static class IndexPlanner
    {
        /// <summary>An index reduced to what the target engine supports, with its names already resolved.</summary>
        internal sealed class PlannedIndex
        {
            /// <summary>Final index name, already fitted to the engine's identifier limit.</summary>
            public string Name { get; set; }

            /// <summary>Owning table, as an unquoted SQL name.</summary>
            public string TableName { get; set; }

            /// <summary>Key columns in index order, as unquoted SQL column names.</summary>
            public List<PlannedIndexColumn> Columns { get; set; } = [];

            /// <summary>Covering columns as unquoted SQL column names; empty when unsupported or unset.</summary>
            public List<string> IncludeColumns { get; set; } = [];

            public bool IsUnique { get; set; }

            /// <summary>Intent token, or null once degraded to the engine's default method.</summary>
            public string Method { get; set; }

            /// <summary>Engine-specific access method, overriding <see cref="Method"/> when set.</summary>
            public string RawMethod { get; set; }

            /// <summary>Partial-index predicate, or null when unsupported or unset.</summary>
            public string Where { get; set; }
        }

        /// <summary>A key column and how it is ordered, once the engine's capabilities are applied.</summary>
        internal sealed class PlannedIndexColumn
        {
            public string Name { get; set; }
            public bool Descending { get; set; }

            /// <summary>A <c>DbIndexNulls</c> token, or null to leave the engine's default ordering.</summary>
            public string Nulls { get; set; }
        }

        /// <summary>Outcome of planning one index.</summary>
        internal sealed class IndexPlanResult
        {
            /// <summary>The planned index, or null when <see cref="Errors"/> is non-empty.</summary>
            public PlannedIndex Index { get; set; }

            /// <summary>Options dropped because the engine cannot express them. Advisory.</summary>
            public List<string> Warnings { get; } = [];

            /// <summary>
            /// Reasons the index cannot be emitted at all. Non-empty means the generator must not fall back
            /// to a weaker index: doing so would change what the schema enforces.
            /// </summary>
            public List<string> Errors { get; } = [];
        }

        /// <summary>
        /// Plans <paramref name="index"/> for an engine with the given capabilities.
        /// </summary>
        /// <param name="resolveColumn">
        /// Maps a C# property name to its database column name. Supplied by the generator so index and
        /// constraint columns resolve identically.
        /// </param>
        /// <param name="maxIdentifierLength">The engine's identifier limit, in bytes.</param>
        public static IndexPlanResult Plan(
            DbIndex index,
            IndexCapabilities capabilities,
            Func<string, string> resolveColumn,
            int maxIdentifierLength)
        {
            if (index == null) throw new ArgumentNullException(nameof(index));
            if (resolveColumn == null) throw new ArgumentNullException(nameof(resolveColumn));

            var result = new IndexPlanResult();
            var where = index.TableName ?? "<unknown table>";

            var keyColumns = (index.Columns ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            if (keyColumns.Count == 0)
            {
                result.Errors.Add($"Index on \"{where}\" has no columns.");
                return result;
            }

            var planned = new PlannedIndex
            {
                TableName = index.TableName,
                RawMethod = index.RawMethod,
            };

            // --- uniqueness: never silently weakened ---
            if (index.IsUnique && !capabilities.HasFlag(IndexCapabilities.Unique))
            {
                result.Errors.Add(
                    $"Index \"{index.Name ?? string.Join(",", keyColumns)}\" on \"{where}\" is unique, but the " +
                    "target database engine has no unique indexes. Emitting a non-unique index would stop the " +
                    "database enforcing the uniqueness the model declares.");
                return result;
            }
            planned.IsUnique = index.IsUnique;

            // --- partial predicate: droppable on a plain index, never on a unique one ---
            if (!string.IsNullOrWhiteSpace(index.Where))
            {
                if (capabilities.HasFlag(IndexCapabilities.Partial))
                {
                    planned.Where = index.Where;
                }
                else if (index.IsUnique)
                {
                    result.Errors.Add(
                        $"Unique index on \"{where}\" is restricted to rows matching \"{index.Where}\", but the " +
                        "target database engine has no partial indexes. Indexing every row would enforce " +
                        "uniqueness over rows the filter deliberately excludes.");
                    return result;
                }
                else
                {
                    result.Warnings.Add(
                        $"Index on \"{where}\": the filter \"{index.Where}\" is not supported by the target " +
                        "database engine and was dropped. The index covers every row, which costs space and " +
                        "write time but returns the same results.");
                }
            }

            // --- access method: degrade to the engine's default ordered index ---
            planned.Method = ResolveMethod(index, capabilities, where, result.Warnings);

            // --- key columns and their ordering ---
            bool canDescend = capabilities.HasFlag(IndexCapabilities.Descending);
            bool canOrderNulls = capabilities.HasFlag(IndexCapabilities.NullsOrdering);
            var descending = ToSet(index.DescendingColumns);
            var nullsFirst = ToSet(index.NullsFirstColumns);
            var nullsLast = ToSet(index.NullsLastColumns);

            if (!canDescend && descending.Count > 0)
                result.Warnings.Add(
                    $"Index on \"{where}\": descending order is not supported by the target database engine " +
                    "and was dropped. Only the scan direction is affected, not the rows returned.");

            if (!canOrderNulls && (nullsFirst.Count > 0 || nullsLast.Count > 0))
                result.Warnings.Add(
                    $"Index on \"{where}\": NULL ordering is not supported by the target database engine and " +
                    "was dropped.");

            foreach (var property in keyColumns)
            {
                string columnName = resolveColumn(property);
                planned.Columns.Add(new PlannedIndexColumn
                {
                    Name = columnName,
                    Descending = canDescend && descending.Contains(property),
                    Nulls = !canOrderNulls ? null
                          : nullsFirst.Contains(property) ? DbIndexNulls.First
                          : nullsLast.Contains(property) ? DbIndexNulls.Last
                          : null,
                });
            }

            // --- covering columns ---
            var include = (index.IncludeColumns ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            if (include.Count > 0)
            {
                if (capabilities.HasFlag(IndexCapabilities.Include))
                    planned.IncludeColumns = include.Select(resolveColumn).ToList();
                else
                    result.Warnings.Add(
                        $"Index on \"{where}\": covering columns ({string.Join(", ", include)}) are not " +
                        "supported by the target database engine and were dropped. Queries reading them still " +
                        "work, but have to visit the table.");
            }

            planned.Name = ResolveName(index, planned, maxIdentifierLength);
            result.Index = planned;
            return result;
        }

        /// <summary>
        /// Maps the intent token to what the engine supports, falling back to its default ordered index (and
        /// warning) when it cannot honour the intent. A <c>RawMethod</c> is the caller's explicit choice and
        /// is passed through untouched.
        /// </summary>
        private static string ResolveMethod(
            DbIndex index, IndexCapabilities capabilities, string table, List<string> warnings)
        {
            if (!string.IsNullOrWhiteSpace(index.RawMethod)) return null;
            if (string.IsNullOrWhiteSpace(index.Method) || index.Method == DbIndexMethods.Default) return null;

            var required = index.Method switch
            {
                DbIndexMethods.Hash       => IndexCapabilities.Hash,
                DbIndexMethods.FullText   => IndexCapabilities.FullText,
                DbIndexMethods.Spatial    => IndexCapabilities.Spatial,
                DbIndexMethods.Contains   => IndexCapabilities.Contains,
                DbIndexMethods.BlockRange => IndexCapabilities.BlockRange,
                _                         => IndexCapabilities.None,
            };

            if (required == IndexCapabilities.None)
            {
                warnings.Add(
                    $"Index on \"{table}\": unknown index method \"{index.Method}\"; the default index method " +
                    "was used instead.");
                return null;
            }

            if (capabilities.HasFlag(required)) return index.Method;

            warnings.Add(
                $"Index on \"{table}\": the target database engine has no {DescribeMethod(index.Method)} index, " +
                "so the default index method was used instead. Queries still return the same results, but may " +
                "not use this index.");
            return null;
        }

        private static string DescribeMethod(string method) => method switch
        {
            DbIndexMethods.Hash       => "hash",
            DbIndexMethods.FullText   => "full-text",
            DbIndexMethods.Spatial    => "spatial",
            DbIndexMethods.Contains   => "containment",
            DbIndexMethods.BlockRange => "block-range",
            _                         => "matching",
        };

        /// <summary>
        /// Keeps an explicit name, otherwise derives one from the table and key columns, disambiguated by a
        /// hash of the options when the index carries any, and fitted to the engine's identifier limit.
        /// </summary>
        /// <remarks>
        /// Two indexes can cover the same columns and differ only in filter, method, covering columns or
        /// ordering, so a name built from the columns alone would collide and the second CREATE would fail.
        /// The name must also be reproducible across processes: a DOWN script's DROP has to match a name a
        /// previous run emitted.
        /// </remarks>
        private static string ResolveName(DbIndex index, PlannedIndex planned, int maxIdentifierLength)
        {
            if (!string.IsNullOrWhiteSpace(index.Name))
                return StableName.Truncate(index.Name, maxIdentifierLength);

            var prefix = planned.IsUnique ? "UX" : "IX";
            var columns = string.Join("_", planned.Columns.Select(c => c.Name));
            var name = $"{prefix}_{planned.TableName}_{columns}";

            // Only the options that actually survived planning discriminate the name; an option the engine
            // dropped must not change it, or the same model would name the index differently per engine.
            var discriminators = new List<string>();
            if (!string.IsNullOrWhiteSpace(planned.Where)) discriminators.Add($"where={planned.Where}");
            if (!string.IsNullOrWhiteSpace(planned.Method)) discriminators.Add($"method={planned.Method}");
            if (!string.IsNullOrWhiteSpace(planned.RawMethod)) discriminators.Add($"raw={planned.RawMethod}");
            if (planned.IncludeColumns.Count > 0)
                discriminators.Add($"include={string.Join(",", planned.IncludeColumns)}");
            foreach (var col in planned.Columns.Where(c => c.Descending || c.Nulls != null))
                discriminators.Add($"order={col.Name}:{(col.Descending ? "desc" : "asc")}:{col.Nulls}");

            if (discriminators.Count > 0)
                name = $"{name}_{StableName.Hash(string.Join("|", discriminators))}";

            return StableName.Truncate(name, maxIdentifierLength);
        }

        private static HashSet<string> ToSet(IEnumerable<string> values) =>
            new HashSet<string>(values ?? [], StringComparer.OrdinalIgnoreCase);
    }
}
