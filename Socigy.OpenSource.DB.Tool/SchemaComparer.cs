using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Socigy.OpenSource.DB.Tool
{
    internal class SchemaComparer
    {
        public static SchemaDiff Compare(DbSchema currentSchema, DbSchema newSchema)
        {
            var diff = new SchemaDiff();

            var currentTables = currentSchema.Tables.ToDictionary(t => t.Name);
            var newTables = newSchema.Tables.ToList();

            // 1. Identify Tables
            var matchedTables = new List<(DbTable Old, DbTable New)>();

            foreach (var newTable in newTables)
            {
                if (currentTables.TryGetValue(newTable.Name, out var oldTable))
                {
                    matchedTables.Add((oldTable, newTable));
                    currentTables.Remove(newTable.Name);
                }
                else if (!string.IsNullOrEmpty(newTable.RenamedFrom) &&
                         ResolveRenamedTable(currentTables, newTable.RenamedFrom) is { } oldRenamedTable)
                {
                    diff.RenamedTables.Add((oldRenamedTable, newTable));
                    matchedTables.Add((oldRenamedTable, newTable));
                    currentTables.Remove(oldRenamedTable.Name);
                }
                else
                {
                    diff.AddedTables.Add(newTable);
                }
            }

            diff.RemovedTables.AddRange(currentTables.Values);

            // 2. Compare Matched Tables (Deep Diff)
            foreach (var pair in matchedTables)
            {
                var alteration = CompareTableInternals(pair.Old, pair.New);

                bool hasSchemaChanges = alteration.AddedColumns.Any() || alteration.RemovedColumns.Any() ||
                                        alteration.ModifiedColumns.Any() || alteration.RenamedColumns.Any() ||
                                        alteration.AddedConstraints.Any() || alteration.RemovedConstraints.Any() ||
                                        alteration.AddedIndexes.Any() || alteration.RemovedIndexes.Any();

                bool hasDataChanges = alteration.AddedRows.Any() || alteration.RemovedRows.Any() ||
                                      alteration.ModifiedRows.Any();

                if (hasSchemaChanges || hasDataChanges)
                {
                    diff.AlteredTables.Add(alteration);
                }
            }

            return diff;
        }

        /// <summary>
        /// Resolves a class-level <c>[Renamed("...")]</c> against the previous schema, accepting EITHER the
        /// database table name or the C# class name.
        ///
        /// Only the database name used to match, while the column-level attribute matched only the C# name —
        /// the same attribute meaning different things at its two valid targets, with nothing in the API
        /// saying so. Accepting both removes the trap: the developer writes whichever name they are looking at.
        /// </summary>
        private static DbTable ResolveRenamedTable(Dictionary<string, DbTable> currentTables, string renamedFrom)
        {
            if (currentTables.TryGetValue(renamedFrom, out var byDatabaseName))
                return byDatabaseName;

            // SourceName is the class's full name ("App.Data.ChatThread"), but a developer writing the
            // attribute reaches for the simple name they see in the file, so accept either.
            var bySourceName = currentTables.Values
                .Where(t => NameMatches(t.SourceName, renamedFrom) || NameMatches(SimpleName(t.SourceName), renamedFrom))
                .ToList();

            if (bySourceName.Count > 1)
                throw new InvalidOperationException(
                    $"[Renamed(\"{renamedFrom}\")] is ambiguous: it matches the previous classes " +
                    $"{string.Join(", ", bySourceName.Select(t => $"'{t.SourceName}' (table \"{t.Name}\")"))}. " +
                    "Use the database table name instead, which is unique.");

            return bySourceName.FirstOrDefault();
        }

        /// <summary>
        /// Resolves a property-level <c>[Renamed("...")]</c> against the previous columns, accepting EITHER
        /// the C# property name or the database column name.
        ///
        /// Only the C# property name used to match — while the generator's own remedy message printed the
        /// database column name, so following the tool's advice verbatim silently did nothing and the column
        /// fell through to a data-destroying DROP + ADD. Consumers reasonably reach for the database name,
        /// because that is what they see in the SQL the tool just showed them.
        /// </summary>
        private static DbColumn ResolveRenamedColumn(IEnumerable<DbColumn> oldColumns, DbColumn newCol, DbTable newTable)
        {
            var matches = oldColumns
                .Where(c => NameMatches(c.SourceName, newCol.RenamedFrom) || NameMatches(c.Name, newCol.RenamedFrom))
                .Distinct()
                .ToList();

            // Two different columns answering to the same name (one by its property name, another by its
            // database name) cannot be resolved by guessing — and guessing wrong here drops a column's data.
            if (matches.Count > 1)
                throw new InvalidOperationException(
                    $"[Renamed(\"{newCol.RenamedFrom}\")] on \"{newTable.Name}\".\"{newCol.Name}\" is ambiguous: it " +
                    $"matches {string.Join(" and ", matches.Select(c => $"\"{c.Name}\" (property '{c.SourceName}')"))}. " +
                    "Name the column unambiguously — the database column name and the C# property name are both accepted.");

            return matches.FirstOrDefault();
        }

        private static bool NameMatches(string candidate, string wanted)
            => !string.IsNullOrEmpty(candidate) && string.Equals(candidate, wanted, StringComparison.Ordinal);

        private static string SimpleName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return fullName;
            int lastDot = fullName.LastIndexOf('.');
            return lastDot >= 0 ? fullName.Substring(lastDot + 1) : fullName;
        }

        private static TableAlteration CompareTableInternals(DbTable oldTable, DbTable newTable)
        {
            var alteration = new TableAlteration { Table = newTable };

            // --- 1. COLUMN COMPARISON ---
            var oldColsMap = oldTable.Columns.ToDictionary(c => c.SourceName ?? c.Name);
            var newCols = newTable.Columns;

            foreach (var newCol in newCols)
            {
                var key = newCol.SourceName ?? newCol.Name;
                DbColumn oldCol = null;
                string matchedKey = null;

                if (oldColsMap.TryGetValue(key, out var foundDirect))
                {
                    oldCol = foundDirect;
                    matchedKey = key;
                }
                else if (!string.IsNullOrEmpty(newCol.RenamedFrom))
                {
                    oldCol = ResolveRenamedColumn(oldColsMap.Values, newCol, newTable);
                    if (oldCol != null) matchedKey = oldCol.SourceName ?? oldCol.Name;
                }

                if (oldCol != null)
                {
                    if (oldCol.Name != newCol.Name)
                        alteration.RenamedColumns.Add(new ColumnRename() { New = newCol, Old = oldCol });

                    var changes = DetectColumnChanges(oldCol, newCol);
                    if (changes.Any())
                        alteration.ModifiedColumns.Add(new ColumnAlteration { OldColumn = oldCol, NewColumn = newCol, Changes = changes });

                    if (matchedKey != null) oldColsMap.Remove(matchedKey);
                }
                else
                {
                    alteration.AddedColumns.Add(newCol);
                }
            }
            alteration.RemovedColumns.AddRange(oldColsMap.Values);

            // --- 2. CONSTRAINT COMPARISON ---
            var oldConstraints = oldTable.Constraints?.ToList() ?? new List<DbConstraint>();
            var newConstraints = newTable.Constraints?.ToList() ?? new List<DbConstraint>();

            foreach (var newCon in newConstraints)
            {
                var match = oldConstraints.FirstOrDefault(oldCon => AreConstraintsFunctionallyEqual(oldCon, newCon));
                if (match != null) oldConstraints.Remove(match);
                else alteration.AddedConstraints.Add(newCon);
            }
            alteration.RemovedConstraints.AddRange(oldConstraints);

            // --- 3. INDEX COMPARISON ---
            // No engine can ALTER an index in place, so a redefined index comes out as a removal plus an
            // addition and the generator emits DROP-then-CREATE.
            var oldIndexes = oldTable.Indexes?.ToList() ?? new List<DbIndex>();
            var newIndexes = newTable.Indexes?.ToList() ?? new List<DbIndex>();

            foreach (var newIndex in newIndexes)
            {
                var match = oldIndexes.FirstOrDefault(oldIndex => DbIndex.AreFunctionallyEqual(oldIndex, newIndex));
                if (match != null) oldIndexes.Remove(match);
                else alteration.AddedIndexes.Add(newIndex);
            }
            alteration.RemovedIndexes.AddRange(oldIndexes);

            // --- 4. DATA (InstantiatedValues) COMPARISON ---
            CompareTableData(oldTable, newTable, alteration);

            return alteration;
        }

        private static void CompareTableData(DbTable oldTable, DbTable newTable, TableAlteration alteration)
        {
            var oldRows = oldTable.InstantiatedValues ?? [];
            var newRows = newTable.InstantiatedValues ?? [];

            if (!oldRows.Any() && !newRows.Any()) return;

            // FIX: Handle Composite Primary Keys (e.g., Many-to-Many link tables)
            var pkColumns = newTable.Columns
                .Where(c => c.IsPrimaryKey == true)
                .OrderBy(c => c.Name) // Sort to ensure consistent key generation
                .ToList();

            // Strategy A: We have PKs -> We can accurately detect Add, Remove, and Modify
            if (pkColumns.Any())
            {
                // Map rows by a composite key string
                var oldMap = new Dictionary<string, Dictionary<string, object?>>();
                foreach (var r in oldRows)
                {
                    var key = GetRowKey(r, pkColumns);
                    // Handle potential duplicate keys in dirty data safely
                    if (!oldMap.ContainsKey(key)) oldMap[key] = r;
                }

                foreach (var newRow in newRows)
                {
                    var key = GetRowKey(newRow, pkColumns);

                    if (oldMap.TryGetValue(key, out var oldRow))
                    {
                        // Row exists in both, check content equality
                        var mismatchedCols = GetMismatchedColumns(oldRow, newRow);
                        if (mismatchedCols.Any())
                        {
                            alteration.ModifiedRows.Add(new RowAlteration
                            {
                                RawOldRow = oldRow,
                                RawNewRow = newRow,
                                ChangedColumns = mismatchedCols
                            });
                        }
                        // Mark as processed
                        oldMap.Remove(key);
                    }
                    else
                    {
                        alteration.RawAddedRows.Add(newRow);
                    }
                }

                // Any rows remaining in oldMap were not found in newRows -> Removed
                alteration.RawRemovedRows.AddRange(oldMap.Values);
            }
            // Strategy B: No PK -> We can only detect Add/Remove based on exact object equality
            else
            {
                foreach (var newRow in newRows)
                {
                    if (!oldRows.Any(oldRow => AreRowsContentEqual(oldRow, newRow)))
                    {
                        alteration.RawAddedRows.Add(newRow);
                    }
                }

                foreach (var oldRow in oldRows)
                {
                    if (!newRows.Any(newRow => AreRowsContentEqual(newRow, oldRow)))
                    {
                        alteration.RawRemovedRows.Add(oldRow);
                    }
                }
            }
        }

        private static string GetRowKey(Dictionary<string, object?> row, List<DbColumn> pkCols)
        {
            // Create a unique string signature: "Val1|Val2|Val3"
            var parts = pkCols.Select(col =>
            {
                if (row.TryGetValue(col.Name, out var val))
                {
                    return Convert.ToString(val, CultureInfo.InvariantCulture);
                }
                return "NULL";
            });
            return string.Join("|", parts);
        }

        private static List<string> GetMismatchedColumns(Dictionary<string, object?> oldRow, Dictionary<string, object?> newRow)
        {
            var diffs = new List<string>();
            var allKeys = oldRow.Keys.Union(newRow.Keys);

            foreach (var key in allKeys)
            {
                oldRow.TryGetValue(key, out var oldVal);
                newRow.TryGetValue(key, out var newVal);

                if (!ValuesMatch(oldVal, newVal))
                {
                    diffs.Add(key);
                }
            }
            return diffs;
        }

        private static bool AreRowsContentEqual(Dictionary<string, object?> a, Dictionary<string, object?> b)
        {
            if (a.Count != b.Count) return false;
            return !GetMismatchedColumns(a, b).Any();
        }

        // FIX: Robust value comparison that handles Type mismatches (Int32 vs Int64), and seed values reloaded from
        // structure.json (where non-null values come back boxed as JsonElement, and a JSON null comes back as a CLR
        // null). The string fallback below normalizes a JsonElement to the same text as the freshly-analyzed CLR
        // value (verified: Convert.ToString(JsonElement) yields "Active"/"1"/"True"), so the round-trip compares
        // equal without special-casing JsonElement.
        private static bool ValuesMatch(object? a, object? b)
        {
            // 1. Handle Nulls
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;

            // 2. Strict Equality
            if (Equals(a, b)) return true;

            // 3. Loose String Equality (Fixes Int32 vs Int64, or Double vs Decimal issues)
            string sa = Convert.ToString(a, CultureInfo.InvariantCulture);
            string sb = Convert.ToString(b, CultureInfo.InvariantCulture);

            return sa == sb;
        }

        private static List<string> DetectColumnChanges(DbColumn oldCol, DbColumn newCol)
        {
            var changes = new List<string>();
            if (oldCol.DatabaseType != newCol.DatabaseType) changes.Add("Type");
            if (oldCol.Nullable != newCol.Nullable) changes.Add("Nullable");
            if (oldCol.DefaultValue != newCol.DefaultValue) changes.Add("Default");
            if (oldCol.IsPrimaryKey != newCol.IsPrimaryKey) changes.Add("PrimaryKey");
            if ((oldCol.IsAutoIncrement == true) != (newCol.IsAutoIncrement == true)) changes.Add("AutoIncrement");

            // Which encryptor — and therefore which key — the column's data lives under. Both sides are bytea,
            // so none of the checks above can see this: without it, moving a column between profiles produced
            // no statement, no warning and no comment, and every affected row threw on its next typed read
            // months later. There is no SQL that can fix it (the key is only reachable from the application),
            // so the generator emits a [SOCIGY:MANUAL] note rather than DDL — the goal is only that the change
            // stops being invisible.
            //
            // A null IsEncrypted on the OLD column means the snapshot predates these fields, not that the
            // column was unencrypted. Skip the comparison in that case and let the value be recorded forward,
            // so upgrading does not fire a one-off warning on every encrypted column in the schema.
            if (oldCol.IsEncrypted != null)
            {
                if ((oldCol.IsEncrypted == true) != (newCol.IsEncrypted == true)
                    || oldCol.EncryptionProfile != newCol.EncryptionProfile)
                    changes.Add("EncryptionProfile");
            }

            return changes;
        }

        private static bool AreConstraintsFunctionallyEqual(DbConstraint a, DbConstraint b)
        {
            if (a.Type != b.Type) return false;

            var aCols = a.Columns ?? Enumerable.Empty<string>();
            var bCols = b.Columns ?? Enumerable.Empty<string>();
            if (!aCols.SequenceEqual(bCols)) return false;

            if (a.Type == "foreign_key")
            {
                if (a.TargetTable != b.TargetTable) return false;
                if (!(a.TargetColumns ?? Enumerable.Empty<string>()).SequenceEqual(b.TargetColumns ?? Enumerable.Empty<string>())) return false;
                if (a.OnDelete != b.OnDelete) return false;
                if (a.OnUpdate != b.OnUpdate) return false;
            }
            else if (a.Type == "check")
            {
                if (a.Value != b.Value) return false;
            }

            return true;
        }
    }
}