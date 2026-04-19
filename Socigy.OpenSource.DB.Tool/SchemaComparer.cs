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
                         currentTables.TryGetValue(newTable.RenamedFrom, out var oldRenamedTable))
                {
                    diff.RenamedTables.Add((oldRenamedTable, newTable));
                    matchedTables.Add((oldRenamedTable, newTable));
                    currentTables.Remove(newTable.RenamedFrom);
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

                // Check if Schema OR Data has changed
                bool hasSchemaChanges = alteration.AddedColumns.Any() || alteration.RemovedColumns.Any() ||
                                        alteration.ModifiedColumns.Any() || alteration.RenamedColumns.Any() ||
                                        alteration.AddedConstraints.Any() || alteration.RemovedConstraints.Any();

                bool hasDataChanges = alteration.AddedRows.Any() || alteration.RemovedRows.Any() ||
                                      alteration.ModifiedRows.Any();

                if (hasSchemaChanges || hasDataChanges)
                {
                    diff.AlteredTables.Add(alteration);
                }
            }

            return diff;
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
                    oldCol = oldColsMap.Values.FirstOrDefault(c => c.SourceName == newCol.RenamedFrom);
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

            // --- 3. DATA (InstantiatedValues) COMPARISON ---
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

        // FIX: Robust value comparison that handles Type mismatches (Int32 vs Int64)
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