using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Core.Migrations;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Socigy.OpenSource.DB.Tool.Generators
{
    public class PostgreSqlGenerator : ISqlGenerator
    {
        /// <summary>Comment prefix prepended to any data-losing statement so it is visible in review and greppable.</summary>
        public const string DestructiveMarker = "-- [SOCIGY:DESTRUCTIVE]";

        /// <summary>
        /// Destructive operations emitted by the last <see cref="Generate"/> call (human-readable). The
        /// orchestrator surfaces these to the user so a data-losing migration is never produced silently.
        /// </summary>
        public IReadOnlyList<string> DestructiveOperations => _destructive;
        private readonly List<string> _destructive = new List<string>();

        /// <inheritdoc/>
        public IReadOnlyList<string> SafetyWarnings => _warnings;
        private readonly List<string> _warnings = new List<string>();

        private void Warn(string detail) => _warnings.Add(detail);

        /// <inheritdoc/>
        /// <remarks>PostgreSQL expresses every index feature the model can describe.</remarks>
        public IndexCapabilities IndexSupport => IndexCapabilities.All;

        /// <inheritdoc/>
        /// <remarks>PostgreSQL's NAMEDATALEN-1; identifiers longer than this are silently truncated.</remarks>
        public int MaxIdentifierLength => 63;

        /// <summary>Comment prefix for a type change whose in-place cast may fail or lose data (narrowing).</summary>
        public const string LossyMarker = "-- [SOCIGY:LOSSY]";

        private void Destructive(string detail, List<string> sink)
        {
            _destructive.Add(detail);
            sink.Add($"{DestructiveMarker} {detail}");
        }

        private void Lossy(string detail, List<string> sink)
        {
            _destructive.Add(detail);
            sink.Add($"{LossyMarker} {detail}");
        }

        // Known-safe widenings within a type family — the in-place cast cannot lose data. Anything else
        // (narrowing, or a conversion between unrelated families) is flagged for review.
        private static bool IsSafeWidening(string fromType, string toType)
        {
            string f = (fromType ?? "").Trim().ToLowerInvariant();
            string t = (toType ?? "").Trim().ToLowerInvariant();
            if (f == t) return true;
            if (t == "text") return true; // text has no length limit

            int IntRank(string s) => s switch { "smallint" => 1, "integer" => 2, "bigint" => 3, _ => 0 };
            int FloatRank(string s) => s switch { "real" => 1, "double precision" => 2, _ => 0 };

            if (IntRank(f) > 0 && IntRank(t) > 0) return IntRank(t) >= IntRank(f);
            if (FloatRank(f) > 0 && FloatRank(t) > 0) return FloatRank(t) >= FloatRank(f);
            return false;
        }

        public (IEnumerable<string> Up, IEnumerable<string> Down) Generate(SchemaDiff diff, bool isFirstMigration)
        {
            var upCommands = new List<string>();
            var downCommands = new List<string>();
            _destructive.Clear();
            _warnings.Clear();
            CollectSafetyWarnings(diff);

            // --- UP: 1. Drop Removed Tables ---
            // --- DOWN: 5. Re-Create Removed Tables & Restore Data ---
            var removedTableForeignKeys = new List<(string Table, DbConstraint Fk)>();
            foreach (var table in diff.RemovedTables)
            {
                Destructive($"Drops table \"{table.Name}\" and ALL its rows (CASCADE also drops dependent " +
                            "objects). The DOWN script restores the schema and seed data only — runtime rows " +
                            "are NOT recoverable.", upCommands);
                upCommands.Add($"DROP TABLE IF EXISTS {Quote(table.Name)} CASCADE;");

                // The sequences go AFTER the table: the column's DEFAULT nextval(...) makes the table depend
                // on the sequence, and DROP TABLE ... CASCADE does not cover it. A sequence another table
                // still uses is left alone (see GenerateDropUnsharedSequences), so this cannot orphan a
                // survivor's column default.
                foreach (var seqDown in GenerateDropUnsharedSequences(table))
                    upCommands.Add(seqDown);

                // Down: Recreate Schema (GenerateCreateTable deliberately excludes foreign keys)
                // An [AutoIncrement] column is recreated with DEFAULT nextval(...), so its sequence has to
                // exist first. IF NOT EXISTS keeps this a no-op for a sequence the UP deliberately kept.
                foreach (var seqUp in GenerateCreateSequences(table))
                    downCommands.Add(seqUp);

                downCommands.Add(GenerateCreateTable(table));

                // The UP's DROP TABLE took the table's indexes with it, so the DOWN has to put them back.
                // They come after the table for the obvious reason, and there is no matching UP statement.
                foreach (var index in table.Indexes ?? [])
                {
                    var planned = PlanIndex(index, table);
                    if (planned != null) downCommands.Add(GenerateCreateIndex(planned));
                }

                // Down: Restore Data (InstantiatedValues)
                if (table.InstantiatedValues != null && table.InstantiatedValues.Any())
                {
                    foreach (var row in table.InstantiatedValues)
                    {
                        downCommands.Add(GenerateInsertStatement(table.Name, row));
                    }
                }

                // Collect this dropped table's foreign keys to re-add AFTER every removed table is recreated, so
                // both endpoints exist (mirroring the AddedTables FK pass). Without this, rolling back a table drop
                // restored the table WITHOUT its foreign keys — the schema differs from before the migration.
                if (table.Constraints != null)
                    foreach (var fk in table.Constraints.Where(c => c.Type == "foreign_key"))
                        removedTableForeignKeys.Add((table.Name, fk));
            }
            foreach (var (fkTable, fk) in removedTableForeignKeys)
                downCommands.Add(GenerateAddConstraint(fkTable, fk));

            // --- UP: 2. Rename Tables ---
            // --- DOWN: 4. Rename Tables Back ---
            foreach (var (oldTable, newTable) in diff.RenamedTables)
            {
                upCommands.Add($"ALTER TABLE {Quote(oldTable.Name)} RENAME TO {Quote(newTable.Name)};");
                downCommands.Insert(0, $"ALTER TABLE {Quote(newTable.Name)} RENAME TO {Quote(oldTable.Name)};");
            }

            // --- UP: 3. Create New Tables & Insert Data ---
            // --- DOWN: 3. Drop New Tables ---
            foreach (var table in diff.AddedTables)
            {
                // Sequences must be created before the table that references them
                foreach (var seqUp in GenerateCreateSequences(table))
                    upCommands.Add(seqUp);

                upCommands.Add(GenerateCreateTable(table));

                // Up: Insert Initial Data
                if (table.InstantiatedValues != null && table.InstantiatedValues.Any())
                {
                    foreach (var row in table.InstantiatedValues)
                    {
                        upCommands.Add(GenerateInsertStatement(table.Name, row));
                    }
                }

                // Indexes come after the table and its seed data: the index has to have something to index,
                // and building it once over the finished rows beats maintaining it through every INSERT.
                // No DOWN counterpart is needed — dropping the table drops its indexes with it.
                foreach (var index in table.Indexes ?? [])
                {
                    var planned = PlanIndex(index, table);
                    if (planned != null) upCommands.Add(GenerateCreateIndex(planned));
                }

                // The migration bookkeeping table is infrastructure, not user schema: the executor writes the
                // IsRollback row into it inside the SAME transaction as the DOWN script, so a DOWN that drops
                // it can never commit. Leave it (and its sequence) standing, as EF Core does with
                // __EFMigrationsHistory. Without this, rolling back the root migration always failed.
                if (table.Name == MigrationHistory.TableName)
                    continue;

                // Insert(0) reverses emission order, so the sequence drops are emitted FIRST to end up
                // BEHIND the DROP TABLE. The column's DEFAULT nextval(...) makes the table depend on the
                // sequence (not the reverse), so DROP TABLE ... CASCADE does not cover the sequence and
                // dropping the sequence first fails with "other objects depend on it".
                foreach (var seqDown in GenerateDropUnsharedSequences(table))
                    downCommands.Insert(0, seqDown);

                downCommands.Insert(0, $"DROP TABLE IF EXISTS {Quote(table.Name)} CASCADE;");
            }

            // --- UP: 4. Alter Tables (Schema & Data) ---
            // --- DOWN: 2. Revert Alterations ---
            foreach (var alteration in diff.AlteredTables)
            {
                // A. Indexes to drop. These go FIRST in the UP: an index over a column this migration is about
                // to drop or retype has to be out of the way, and a redefined index (same name, new shape)
                // arrives as a removal plus an addition, so the DROP must precede its CREATE.
                var indexDrops = new List<string>();
                var indexDropUndo = new List<string>();
                foreach (var index in alteration.RemovedIndexes ?? [])
                {
                    var planned = PlanIndex(index, alteration.Table);
                    if (planned == null) continue;

                    Warn($"Drops index \"{planned.Name}\" on \"{alteration.Table.Name}\". Rebuilding it on a " +
                         "large table is expensive, and queries relying on it will be slower until it is back.");
                    indexDrops.Add(GenerateDropIndex(planned.Name));
                    indexDropUndo.Add(GenerateCreateIndex(planned));
                }
                upCommands.AddRange(indexDrops);

                // B. Schema Changes
                var (schemaUps, schemaDowns) = GenerateTableAlterations(alteration);
                upCommands.AddRange(schemaUps);

                // C. Data Changes (Rows Added/Removed/Modified)
                var (dataUps, dataDowns) = GenerateDataAlterations(alteration);
                upCommands.AddRange(dataUps);

                // D. Indexes to create, last in the UP so every column they reference exists and the index is
                // built once over the final rows instead of being maintained through the data changes above.
                var indexCreateUndo = new List<string>();
                foreach (var index in alteration.AddedIndexes ?? [])
                {
                    var planned = PlanIndex(index, alteration.Table);
                    if (planned == null) continue;

                    upCommands.Add(GenerateCreateIndex(planned));
                    indexCreateUndo.Add(GenerateDropIndex(planned.Name));
                }

                // The DOWN mirrors it: drop what the UP created, revert the data and then the schema, and
                // finally rebuild the indexes the UP dropped, by which point their columns are back.
                var alterationDown = new List<string>();
                alterationDown.AddRange(indexCreateUndo);
                alterationDown.AddRange(dataDowns);
                alterationDown.AddRange(schemaDowns);
                alterationDown.AddRange(indexDropUndo);

                for (int i = alterationDown.Count - 1; i >= 0; i--)
                    downCommands.Insert(0, alterationDown[i]);
            }

            // --- UP: 5. Add Foreign Keys for New Tables ---
            // --- DOWN: 1. Drop Foreign Keys ---
            foreach (var table in diff.AddedTables.Where(x => x.Constraints != null))
            {
                var fks = table.Constraints.Where(c => c.Type == "foreign_key");
                foreach (var fk in fks)
                {
                    upCommands.Add(GenerateAddConstraint(table.Name, fk));
                    var fkName = !string.IsNullOrEmpty(fk.Name) ? fk.Name : GuessConstraintName(fk);
                    downCommands.Insert(0, $"ALTER TABLE {Quote(table.Name)} DROP CONSTRAINT IF EXISTS {Quote(fkName)};");
                }
            }

            // uuid_generate_v1mc() (emitted for a Guid.Sequential default) lives in the uuid-ossp extension, which
            // is not installed by default — without this the CREATE TABLE / ALTER fails to apply with
            // "function uuid_generate_v1mc() does not exist". Ensure the extension first. (gen_random_uuid(), used
            // by Guid.Random, is built in to PostgreSQL 13+ and needs no extension.)
            PrependUuidOsspExtensionIfNeeded(upCommands);
            PrependUuidOsspExtensionIfNeeded(downCommands);

            return (upCommands, downCommands);
        }

        // Advisory checks that don't change the generated SQL but warn about migrations that can fail at apply
        // time or silently lose data. Runs after ProvideDefaults(), so the alteration lists are non-null.
        private void CollectSafetyWarnings(SchemaDiff diff)
        {
            foreach (var alteration in diff.AlteredTables)
            {
                var table = alteration.Table.Name;

                // A NOT NULL column added with no default fails the moment the table already has rows.
                foreach (var c in alteration.AddedColumns)
                {
                    if (c.Nullable == false && c.IsAutoIncrement != true && string.IsNullOrEmpty(c.DefaultValue))
                        Warn($"Adds NOT NULL column \"{table}\".\"{c.Name}\" without a default; this fails if \"{table}\" " +
                             "already contains rows. Add a [Default], make the property nullable, or backfill before applying.");
                }

                // SET NOT NULL on an existing column with no default has the same hazard for existing NULLs.
                foreach (var mod in alteration.ModifiedColumns)
                {
                    if (mod.Changes.Contains("Nullable") && mod.NewColumn.Nullable == false
                        && mod.NewColumn.IsAutoIncrement != true && string.IsNullOrEmpty(mod.NewColumn.DefaultValue))
                        Warn($"Sets \"{table}\".\"{mod.NewColumn.Name}\" NOT NULL with no default; this fails if existing rows " +
                             "hold NULL in that column. Backfill the column or add a [Default] first.");
                }

                // A column dropped while another of the same type+nullability is added is the classic unmarked
                // rename: the data is dropped instead of carried over. Flag it (the fix is a [Renamed] attribute).
                foreach (var removed in alteration.RemovedColumns)
                    foreach (var added in alteration.AddedColumns)
                    {
                        if (string.Equals(removed.DatabaseType, added.DatabaseType, StringComparison.OrdinalIgnoreCase)
                            && removed.Nullable == added.Nullable)
                            Warn($"Column \"{table}\".\"{removed.Name}\" is dropped and \"{added.Name}\" added with the same " +
                                 $"type ({added.DatabaseType}). If this is a rename, set [Renamed(\"{removed.Name}\")] on the new " +
                                 "property so the data is preserved instead of dropped.");
                    }
            }
        }

        private string GenerateCreateTable(DbTable table)
        {
            var sb = new StringBuilder();

            // The migration bookkeeping table is the one table a DOWN script deliberately leaves standing (the
            // rollback row is written into it in the same transaction), so rolling the first migration back and
            // then forward again re-runs this exact statement against a table that still exists. Guard it the
            // same way its sequence beside it already is. Every other table is left unguarded on purpose: one
            // that already exists is a real conflict and should fail loudly rather than silently keep whatever
            // shape it happens to have.
            var ifNotExists = table.Name == MigrationHistory.TableName ? "IF NOT EXISTS " : "";
            sb.AppendLine($"CREATE TABLE {ifNotExists}{Quote(table.Name)} (");

            var lines = new List<string>();

            // A. Columns
            foreach (var col in table.Columns)
            {
                lines.Add("    " + GenerateColumnDefinitionForTable(table, col));
            }

            // B. Constraints (Check, Unique) - Exclude FKs (deferred)
            if (table.Constraints != null)
                foreach (var constraint in table.Constraints.Where(c => c.Type != "foreign_key"))
                {
                    lines.Add("    " + GenerateConstraintDefinition(constraint, table));
                }

            // C. Primary Keys (Aggregated from Columns), ordered by the composite key position so a PK whose key
            // order differs from the column declaration order is emitted correctly. A null/equal order keeps the
            // column declaration order (stable sort), matching the prior behavior for ordinary keys.
            var pkColumns = table.Columns.Where(c => c.IsPrimaryKey == true)
                .OrderBy(c => c.PrimaryKeyOrder ?? int.MaxValue).ToList();
            if (pkColumns.Any())
            {
                var pkName = $"PK_{table.Name}";
                var cols = string.Join(", ", pkColumns.Select(c => Quote(c.Name)));
                lines.Add($"    CONSTRAINT {Quote(pkName)} PRIMARY KEY ({cols})");
            }

            sb.Append(string.Join(",\n", lines));
            sb.Append("\n);");

            return sb.ToString();
        }

        private (List<string> Up, List<string> Down) GenerateTableAlterations(TableAlteration alteration)
        {
            var up = new List<string>();
            var down = new List<string>();
            // The DOWN re-add of a removed constraint must run AFTER any removed COLUMN it references has been
            // re-added (section 2 below). Since the DOWN block executes in this list's order, defer these to the
            // end — otherwise dropping a UNIQUE/CHECK/FK and its column together produced a DOWN that re-added the
            // constraint before the column ("column ... does not exist"), failing the rollback.
            var removedConstraintReAdds = new List<string>();

            var tableName = Quote(alteration.Table.Name);

            // 1. Removed Constraints
            foreach (var c in alteration.RemovedConstraints)
            {
                up.Add($"ALTER TABLE {tableName} DROP CONSTRAINT IF EXISTS {Quote(c.Name)};");
                removedConstraintReAdds.Add(GenerateAddConstraint(alteration.Table.Name, c));
            }

            // 2. Removed Columns
            foreach (var c in alteration.RemovedColumns)
            {
                Destructive($"Drops column \"{alteration.Table.Name}\".\"{c.Name}\"; its data cannot be " +
                            "recovered by the DOWN script.", up);
                up.Add($"ALTER TABLE {tableName} DROP COLUMN {Quote(c.Name)};");
                down.Add($"ALTER TABLE {tableName} ADD COLUMN {GenerateColumnDefinition(c, alteration.Table.Name)};");
            }

            // 3. Added Columns (create sequences for new auto-increment columns first)
            foreach (var c in alteration.AddedColumns)
            {
                if (c.IsAutoIncrement == true)
                {
                    var seqName = GetSequenceName(alteration.Table.Name, c);
                    // Type the sequence to the column (smallint/integer/bigint), matching the CREATE TABLE path.
                    var seqType = GetSequenceType(c);
                    up.Add(seqType == null
                        ? $"CREATE SEQUENCE IF NOT EXISTS {Quote(seqName)};"
                        : $"CREATE SEQUENCE IF NOT EXISTS {Quote(seqName)} AS {seqType};");
                    // DOWN: drop the column (it depends on the sequence) BEFORE dropping the sequence, else the
                    // sequence drop fails with "other objects depend on it".
                    down.Add($"ALTER TABLE {tableName} DROP COLUMN {Quote(c.Name)};");
                    down.Add($"DROP SEQUENCE IF EXISTS {Quote(seqName)};");
                }
                else
                {
                    down.Add($"ALTER TABLE {tableName} DROP COLUMN {Quote(c.Name)};");
                }
                up.Add($"ALTER TABLE {tableName} ADD COLUMN {GenerateColumnDefinition(c, alteration.Table.Name)};");
            }

            // 4. Renamed Columns
            // The DOWN rename-back must run AFTER the modified-column reverts (section 5), which reference the column
            // by its NEW name, and BEFORE the PK re-add (section 6), which references it by its OLD name. A column can
            // be renamed AND modified in one diff, so emitting the rename-back here (before section 5) made the modify
            // revert target a column that the rename-back had already renamed away ("column ... does not exist").
            // Collect the rename-backs and splice them in after section 5 instead.
            var renameDowns = new List<string>();
            foreach (var renaming in alteration.RenamedColumns)
            {
                up.Add($"ALTER TABLE {tableName} RENAME COLUMN {Quote(renaming.Old.Name)} TO {Quote(renaming.New.Name)};");
                renameDowns.Add($"ALTER TABLE {tableName} RENAME COLUMN {Quote(renaming.New.Name)} TO {Quote(renaming.Old.Name)};");
            }

            // 5. Modified Columns
            foreach (var mod in alteration.ModifiedColumns)
            {
                var colName = Quote(mod.NewColumn.Name);

                foreach (var change in mod.Changes)
                {
                    if (change == "PrimaryKey") continue;

                    switch (change)
                    {
                        case "Type":
                            var newType = mod.NewColumn.DatabaseType;
                            var oldType = mod.OldColumn.DatabaseType;
                            if (!IsSafeWidening(oldType, newType))
                                Lossy($"Casts \"{alteration.Table.Name}\".\"{mod.NewColumn.Name}\" {oldType} -> {newType} " +
                                      "in place; the cast may fail or lose data on existing rows.", up);
                            up.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} TYPE {newType} USING {colName}::{newType};");
                            if (!IsSafeWidening(newType, oldType))
                                Lossy($"Casts \"{alteration.Table.Name}\".\"{mod.NewColumn.Name}\" {newType} -> {oldType} " +
                                      "in place; the cast may fail or lose data on existing rows.", down);
                            down.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} TYPE {oldType} USING {colName}::{oldType};");
                            break;

                        case "Nullable":
                            var upAction = mod.NewColumn.Nullable == true ? "DROP NOT NULL" : "SET NOT NULL";
                            up.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} {upAction};");
                            var downAction = mod.OldColumn.Nullable == true ? "DROP NOT NULL" : "SET NOT NULL";
                            down.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} {downAction};");
                            break;

                        case "Default":
                            // Route through TranslateDefault like the CREATE TABLE / ADD COLUMN paths, otherwise
                            // a token such as $socigy$guid.random is emitted verbatim and the migration fails at
                            // apply ("unterminated dollar-quoted string").
                            if (string.IsNullOrEmpty(mod.NewColumn.DefaultValue))
                                up.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} DROP DEFAULT;");
                            else
                                up.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} SET DEFAULT {TranslateDefault(mod.NewColumn.DefaultValue)};");

                            if (string.IsNullOrEmpty(mod.OldColumn.DefaultValue))
                                down.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} DROP DEFAULT;");
                            else
                                down.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} SET DEFAULT {TranslateDefault(mod.OldColumn.DefaultValue)};");
                            break;

                        case "AutoIncrement":
                            if (mod.NewColumn.IsAutoIncrement == true)
                            {
                                // Adding AutoIncrement: create sequence then set DEFAULT
                                var addSeqName = GetSequenceName(alteration.Table.Name, mod.NewColumn);
                                var addSeqType = GetSequenceType(mod.NewColumn) ?? "INTEGER";
                                up.Add($"CREATE SEQUENCE IF NOT EXISTS {Quote(addSeqName)} AS {addSeqType};");
                                up.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} SET DEFAULT nextval('{addSeqName}');");
                                down.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} DROP DEFAULT;");
                                down.Add($"DROP SEQUENCE IF EXISTS {Quote(addSeqName)};");
                            }
                            else
                            {
                                // Removing AutoIncrement: drop DEFAULT then drop sequence
                                var dropSeqName = GetSequenceName(alteration.Table.Name, mod.OldColumn);
                                up.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} DROP DEFAULT;");
                                up.Add($"DROP SEQUENCE IF EXISTS {Quote(dropSeqName)};");
                                var dropSeqType = GetSequenceType(mod.OldColumn) ?? "INTEGER";
                                down.Add($"CREATE SEQUENCE IF NOT EXISTS {Quote(dropSeqName)} AS {dropSeqType};");
                                down.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} SET DEFAULT nextval('{dropSeqName}');");
                            }
                            break;
                    }
                }
            }

            // Rename-backs run after the modified-column reverts (which use the new name) and before the PK re-add
            // below (which uses the old name).
            down.AddRange(renameDowns);

            // 6. Primary Key Changes
            bool pkChanged = alteration.ModifiedColumns.Any(m => m.Changes.Contains("PrimaryKey"))
                             || alteration.AddedColumns.Any(c => c.IsPrimaryKey == true)
                             || alteration.RemovedColumns.Any(c => c.IsPrimaryKey == true);

            if (pkChanged)
            {
                var pkName = $"PK_{alteration.Table.Name}";
                up.Add($"ALTER TABLE {tableName} DROP CONSTRAINT IF EXISTS {Quote(pkName)};");

                var newPkCols = alteration.Table.Columns.Where(c => c.IsPrimaryKey == true).ToList();
                if (newPkCols.Any())
                {
                    var cols = string.Join(", ", newPkCols.Select(c => Quote(c.Name)));
                    up.Add($"ALTER TABLE {tableName} ADD CONSTRAINT {Quote(pkName)} PRIMARY KEY ({cols});");
                }
                down.Add($"ALTER TABLE {tableName} DROP CONSTRAINT IF EXISTS {Quote(pkName)};");

                // DOWN must restore the OLD primary key, not just drop the new one (otherwise rollback leaves
                // the table with no primary key). Reconstruct the old PK column set from the diff: unchanged PK
                // columns + columns whose PK flag changed away + removed-but-was-PK columns.
                var modifiedNames = new HashSet<string>(alteration.ModifiedColumns.Select(m => m.NewColumn.Name));
                var addedNames = new HashSet<string>(alteration.AddedColumns.Select(c => c.Name));
                var oldPkCols = new List<string>();
                foreach (var c in alteration.Table.Columns)
                    if (c.IsPrimaryKey == true && !modifiedNames.Contains(c.Name) && !addedNames.Contains(c.Name))
                        oldPkCols.Add(c.Name);
                foreach (var m in alteration.ModifiedColumns)
                    if (m.OldColumn.IsPrimaryKey == true)
                        oldPkCols.Add(m.OldColumn.Name);
                foreach (var c in alteration.RemovedColumns)
                    if (c.IsPrimaryKey == true)
                        oldPkCols.Add(c.Name);
                if (oldPkCols.Count > 0)
                {
                    var oldCols = string.Join(", ", oldPkCols.Select(Quote));
                    down.Add($"ALTER TABLE {tableName} ADD CONSTRAINT {Quote(pkName)} PRIMARY KEY ({oldCols});");
                }
            }

            // 7. Added Constraints
            foreach (var c in alteration.AddedConstraints)
            {
                up.Add(GenerateAddConstraint(alteration.Table.Name, c));
                down.Add($"ALTER TABLE {tableName} DROP CONSTRAINT IF EXISTS {Quote(c.Name)};");
            }

            // Re-add removed constraints LAST in the DOWN, after the columns they may reference are re-created.
            down.AddRange(removedConstraintReAdds);
            return (up, down);
        }
        private (List<string> Up, List<string> Down) GenerateDataAlterations(TableAlteration alteration)
        {
            var up = new List<string>();
            var down = new List<string>();
            var tableName = alteration.Table.Name;

            // 1. Added Rows
            foreach (var row in alteration.RawAddedRows)
            {
                up.Add(GenerateInsertStatement(tableName, row));
                down.Add(GenerateDeleteStatement(tableName, row, alteration.Table));
            }

            // 2. Removed Rows
            foreach (var row in alteration.RawRemovedRows)
            {
                up.Add(GenerateDeleteStatement(tableName, row, alteration.Table));
                down.Add(GenerateInsertStatement(tableName, row));
            }

            // 3. Modified Rows
            foreach (var rowMod in alteration.ModifiedRows)
            {
                up.Add(GenerateUpdateStatement(tableName, rowMod.RawNewRow, alteration.Table, rowMod.ChangedColumns));
                // Restore old values
                down.Add(GenerateUpdateStatement(tableName, rowMod.RawOldRow, alteration.Table, rowMod.ChangedColumns));
            }

            return (up, down);
        }

        private string GenerateInsertStatement(string tableName, Dictionary<string, object?> row)
        {
            var cols = string.Join(", ", row.Keys.Select(Quote));
            var vals = string.Join(", ", row.Values.Select(FormatSqlValue));
            return $"INSERT INTO {Quote(tableName)} ({cols}) VALUES ({vals});";
        }
        private string GenerateDeleteStatement(string tableName, Dictionary<string, object?> row, DbTable tableDef)
        {
            // Identify PK columns to build the WHERE clause
            var pkCols = tableDef.Columns.Where(c => c.IsPrimaryKey == true).ToList();
            var criteria = new List<string>();

            if (pkCols.Any())
            {
                foreach (var pk in pkCols)
                {
                    if (row.TryGetValue(pk.Name, out var val))
                    {
                        criteria.Add($"{Quote(pk.Name)} = {FormatSqlValue(val)}");
                    }
                }
            }
            else
            {
                // Fallback: If no PK, match ALL columns (safest best effort). A null must use IS NULL, since
                // "col = NULL" is never true and would make the delete (e.g. a seed-row rollback) a silent no-op.
                foreach (var kvp in row)
                {
                    criteria.Add(kvp.Value == null
                        ? $"{Quote(kvp.Key)} IS NULL"
                        : $"{Quote(kvp.Key)} = {FormatSqlValue(kvp.Value)}");
                }
            }

            return $"DELETE FROM {Quote(tableName)} WHERE {string.Join(" AND ", criteria)};";
        }
        private string GenerateUpdateStatement(string tableName, Dictionary<string, object?> row, DbTable tableDef, List<string> changedCols)
        {
            var pkCols = tableDef.Columns.Where(c => c.IsPrimaryKey == true).ToList();
            if (!pkCols.Any()) return $"-- WARNING: Cannot generate UPDATE for {tableName} without Primary Key";

            var updates = new List<string>();
            // Only update columns that actually changed (optimization)
            foreach (var colName in changedCols)
            {
                if (row.TryGetValue(colName, out var val))
                {
                    updates.Add($"{Quote(colName)} = {FormatSqlValue(val)}");
                }
            }

            var whereClauses = new List<string>();
            foreach (var pk in pkCols)
            {
                if (row.TryGetValue(pk.Name, out var val))
                {
                    whereClauses.Add($"{Quote(pk.Name)} = {FormatSqlValue(val)}");
                }
            }

            if (!updates.Any()) return ""; // Nothing to do

            return $"UPDATE {Quote(tableName)} SET {string.Join(", ", updates)} WHERE {string.Join(" AND ", whereClauses)};";
        }

        #region Helper Methods
        private string FormatSqlValue(object? value)
        {
            if (value == null) return "NULL";

            switch (value)
            {
                case string s:
                    // Escape single quotes
                    return $"'{s.Replace("'", "''")}'";
                case bool b:
                    return b ? "TRUE" : "FALSE";
                case Guid g:
                    return $"'{g}'";
                case DateTime dt:
                    return $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'";
                case DateTimeOffset dto:
                    return $"'{dto:yyyy-MM-dd HH:mm:ss.fff zzz}'";
                case byte[] bytes:
                    return $"'\\x{BitConverter.ToString(bytes).Replace("-", "")}'";
                case Enum e:
                    return Convert.ToInt32(e).ToString();
                // The SAVED schema is round-tripped through System.Text.Json, so its seed values come back as
                // JsonElement, not the original CLR type. Branch on the JSON kind directly — a String stays quoted
                // (a numeric-looking [Description] like "404" must NOT lose its quotes and become an int literal
                // into a text column), a Number emits its invariant raw text, and bool/null map correctly. Deciding
                // quoting by re-parsing the text with double.TryParse (below) produced broken/locale-dependent SQL.
                case System.Text.Json.JsonElement je:
                    switch (je.ValueKind)
                    {
                        case System.Text.Json.JsonValueKind.String:
                            return $"'{je.GetString()!.Replace("'", "''")}'";
                        case System.Text.Json.JsonValueKind.Number:
                            return je.GetRawText();
                        case System.Text.Json.JsonValueKind.True:
                            return "TRUE";
                        case System.Text.Json.JsonValueKind.False:
                            return "FALSE";
                        case System.Text.Json.JsonValueKind.Null:
                        case System.Text.Json.JsonValueKind.Undefined:
                            return "NULL";
                        default:
                            return $"'{je.GetRawText().Replace("'", "''")}'";
                    }
                default:
                    // Numbers, etc. Parse culture-invariantly so the quote/no-quote decision is deterministic
                    // across locales (a comma-decimal locale must not change which literals are treated as numbers).
                    if (double.TryParse(value.ToString(), System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                        return Convert.ToString(value, CultureInfo.InvariantCulture);

                    // Fallback to string representation
                    return $"'{value.ToString().Replace("'", "''")}'";
            }
        }

        private string GenerateColumnDefinition(DbColumn col, string? tableName = null)
        {
            var sb = new StringBuilder();
            sb.Append($"{Quote(col.Name)} {col.DatabaseType}");
            // The analyzer marks a nullable column Nullable==true and a NON-nullable one Nullable==null (never
            // false), so treat anything other than an explicit true as NOT NULL — otherwise every required,
            // non-primary-key column would be created NULLABLE, silently dropping the model's NOT NULL contract.
            if (col.Nullable != true) sb.Append(" NOT NULL");

            if (col.IsAutoIncrement == true)
            {
                // Must resolve to the SAME sequence the ALTER path created (it is named with the table). A null
                // tableName here produced "_id_seq", which is never created, so the column default referenced a
                // missing sequence and the migration failed at apply.
                var seqName = GetSequenceName(tableName, col);
                sb.Append($" DEFAULT nextval('{seqName}')");
            }
            else if (!string.IsNullOrEmpty(col.DefaultValue))
            {
                sb.Append($" DEFAULT {TranslateDefault(col.DefaultValue)}");
            }

            return sb.ToString();
        }

        private string GenerateColumnDefinitionForTable(DbTable table, DbColumn col)
        {
            var sb = new StringBuilder();
            sb.Append($"{Quote(col.Name)} {col.DatabaseType}");
            // The analyzer marks a nullable column Nullable==true and a NON-nullable one Nullable==null (never
            // false), so treat anything other than an explicit true as NOT NULL — otherwise every required,
            // non-primary-key column would be created NULLABLE, silently dropping the model's NOT NULL contract.
            if (col.Nullable != true) sb.Append(" NOT NULL");

            if (col.IsAutoIncrement == true)
            {
                var seqName = GetSequenceName(table.Name, col);
                sb.Append($" DEFAULT nextval('{seqName}')");
            }
            else if (!string.IsNullOrEmpty(col.DefaultValue))
            {
                sb.Append($" DEFAULT {TranslateDefault(col.DefaultValue)}");
            }

            return sb.ToString();
        }

        private string GetSequenceName(string? tableName, DbColumn col)
        {
            if (!string.IsNullOrEmpty(col.SequenceName))
                return col.SequenceName;
            return $"{tableName}_{col.Name}_seq";
        }

        private IEnumerable<string> GenerateCreateSequences(DbTable table)
        {
            foreach (var col in table.Columns.Where(c => c.IsAutoIncrement == true))
            {
                var seqType = GetSequenceType(col);
                if (seqType == null)
                {
                    Logger.Error($"[AutoIncrement] on column '{col.Name}' of table '{table.Name}' uses type '{col.DotnetType}' which is not a supported sequence type. Use short, int, or long.");
                    continue;
                }
                yield return $"CREATE SEQUENCE IF NOT EXISTS {Quote(GetSequenceName(table.Name, col))} AS {seqType};";
            }
        }

        /// <summary>
        /// Drops the sequences owned by <paramref name="table"/>, skipping any still referenced by another
        /// table in the current schema. An explicit <c>[AutoIncrement(SequenceName = "...")]</c> can point
        /// several tables at one sequence, and dropping a shared sequence would break the survivors' column
        /// defaults. Skipped sequences are reported through <see cref="SafetyWarnings"/>.
        /// </summary>
        private IEnumerable<string> GenerateDropUnsharedSequences(DbTable table)
        {
            foreach (var col in table.Columns.Where(c => c.IsAutoIncrement == true))
            {
                var seqName = GetSequenceName(table.Name, col);
                var sharedWith = FindSequenceSharers(seqName, table.Name);
                if (sharedWith != null)
                {
                    Warn($"Sequence \"{seqName}\" is left in place when table \"{table.Name}\" is dropped: " +
                         $"table \"{sharedWith}\" still uses it for an [AutoIncrement] column.");
                    continue;
                }

                yield return $"DROP SEQUENCE IF EXISTS {Quote(seqName)};";
            }
        }

        /// <summary>
        /// Name of another table in the current schema whose [AutoIncrement] column resolves to
        /// <paramref name="seqName"/>, or null when the sequence belongs to <paramref name="ownerTable"/> alone.
        /// </summary>
        private string? FindSequenceSharers(string seqName, string ownerTable)
        {
            var tables = Configuration.CurrentSchema?.Tables;
            if (tables == null) return null;

            foreach (var other in tables)
            {
                if (other?.Columns == null || other.Name == ownerTable) continue;
                foreach (var col in other.Columns.Where(c => c.IsAutoIncrement == true))
                {
                    if (GetSequenceName(other.Name, col) == seqName)
                        return other.Name;
                }
            }

            return null;
        }

        private static string? GetSequenceType(DbColumn col)
        {
            var typeName = col.DotnetType?.Split('.').Last().ToLowerInvariant() ?? "";
            return typeName switch
            {
                "int16" or "short" => "SMALLINT",
                "int32" or "int"   => "INTEGER",
                "int64" or "long"  => "BIGINT",
                _                  => null
            };
        }

        private static string TranslateForeignKeyAction(string token)
        {
            if (string.IsNullOrEmpty(token) || !token.StartsWith("$socigy$val$"))
                return token;

            return token switch
            {
                DbValues.ForeignKey.Cascade    => "CASCADE",
                DbValues.ForeignKey.SetNull    => "SET NULL",
                DbValues.ForeignKey.SetDefault => "SET DEFAULT",
                DbValues.ForeignKey.Restrict   => "RESTRICT",
                DbValues.ForeignKey.NoAction   => "NO ACTION",
                _                              => token
            };
        }

        // If any statement uses a uuid-ossp function (uuid_generate_*), prepend a single idempotent
        // CREATE EXTENSION so the migration applies on a database where the extension isn't installed yet.
        private static void PrependUuidOsspExtensionIfNeeded(List<string> commands)
        {
            if (commands.Any(c => c != null && c.Contains("uuid_generate_")))
                commands.Insert(0, "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";");
        }

        private static string TranslateDefault(string token)
        {
            if (string.IsNullOrEmpty(token) || !token.StartsWith("$socigy$"))
                return token;

            return token switch
            {
                DbDefaults.Guid.Random     => "gen_random_uuid()",
                DbDefaults.Guid.Sequential => "uuid_generate_v1mc()",
                DbDefaults.Time.Now        => "timezone('utc', now())",
                DbDefaults.Time.NowLocal   => "now()",
                DbDefaults.Time.Date       => "current_date",
                DbDefaults.Bool.True       => "TRUE",
                DbDefaults.Bool.False      => "FALSE",
                DbDefaults.Number.Zero     => "0",
                DbDefaults.Number.One      => "1",
                DbDefaults.Text.Empty      => "''",
                _                          => token
            };
        }

        private string GenerateConstraintDefinition(DbConstraint con, DbTable sourceTable)
        {
            var sb = new StringBuilder();
            var name = !string.IsNullOrEmpty(con.Name) ? con.Name : GuessConstraintName(con);
            sb.Append($"CONSTRAINT {Quote(name)} ");

            switch (con.Type.ToLower())
            {
                case "unique":
                    var uniqueCols = string.Join(", ", con.Columns.Select(x => Quote(ResolveColumnName(sourceTable, x))));
                    sb.Append($"UNIQUE ({uniqueCols})");
                    break;
                case "check":
                    sb.Append($"CHECK ({con.Value})");
                    break;
                case "foreign_key":
                    var fkCols = string.Join(", ", con.Columns.Select(x => Quote(ResolveColumnName(sourceTable, x))));
                    var targetTable = Configuration.CurrentSchema.Tables.FirstOrDefault(x => x.SourceName == con.TargetTable);
                    var targetTableName = targetTable?.Name ?? con.TargetTable;
                    var targetCols = string.Join(", ", con.TargetColumns.Select(x =>
                        Quote(ResolveColumnName(targetTable, x))));
                    sb.Append($"FOREIGN KEY ({fkCols}) REFERENCES {Quote(targetTableName)} ({targetCols})");
                    if (!string.IsNullOrEmpty(con.OnDelete)) sb.Append($" ON DELETE {TranslateForeignKeyAction(con.OnDelete)}");
                    if (!string.IsNullOrEmpty(con.OnUpdate)) sb.Append($" ON UPDATE {TranslateForeignKeyAction(con.OnUpdate)}");
                    break;
            }
            return sb.ToString();
        }

        private string GenerateAddConstraint(string tableName, DbConstraint constraint)
        {
            return $"ALTER TABLE {Quote(tableName)} ADD {GenerateConstraintDefinition(constraint, Configuration.CurrentSchema.Tables.FirstOrDefault(x => x.Name == tableName))};";
        }

        // When a constraint column can't be matched to a current-table column (e.g. it was renamed, so the old
        // constraint's old property name no longer resolves against the new schema), fall back to the snake_case
        // column name rather than the raw PascalCase property name, which is never a valid column identifier and
        // makes the generated DDL fail at apply. Uses the same policy the source generator uses for column names.
        private static string ToColumnName(string propertyOrColumn)
            => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(propertyOrColumn);

        /// <summary>
        /// Maps a C# property name to its database column name using <paramref name="sourceTable"/>, falling
        /// back to the snake_case convention when the property cannot be matched (e.g. a constraint left over
        /// from a column that has since been renamed).
        /// </summary>
        /// <remarks>
        /// Constraint and index columns are both stored as property names, so both must resolve the same way;
        /// resolving one of them differently would name the same column two ways in one migration.
        /// </remarks>
        private static string ResolveColumnName(DbTable sourceTable, string propertyName)
            => sourceTable?.Columns?.FirstOrDefault(
                   y => y.SourceName != null && y.SourceName.Split('.').Last() == propertyName)?.Name
               ?? ToColumnName(propertyName);

        /// <summary>Translates a portable index-method token to the PostgreSQL access method.</summary>
        private static string TranslateIndexMethod(string token) => token switch
        {
            DbIndexMethods.Hash       => "hash",
            DbIndexMethods.FullText   => "gin",
            DbIndexMethods.Spatial    => "gist",
            DbIndexMethods.Contains   => "gin",
            DbIndexMethods.BlockRange => "brin",
            _                         => null,   // Default / unset: btree, which needs no USING clause
        };

        /// <summary>
        /// Plans <paramref name="index"/> against this engine's capabilities and pipes the planner's findings
        /// into the generator's warning sinks. Returns null when the index cannot be emitted.
        /// </summary>
        private IndexPlanner.PlannedIndex PlanIndex(DbIndex index, DbTable table)
        {
            var plan = IndexPlanner.Plan(
                index, IndexSupport, property => ResolveColumnName(table, property), MaxIdentifierLength);

            foreach (var warning in plan.Warnings)
                Warn(warning);

            foreach (var error in plan.Errors)
                Logger.Error(error);

            return plan.Index;
        }

        /// <summary>Renders <c>CREATE INDEX</c> for an index already reduced to what PostgreSQL supports.</summary>
        private string GenerateCreateIndex(IndexPlanner.PlannedIndex index)
        {
            var sb = new StringBuilder("CREATE ");
            if (index.IsUnique) sb.Append("UNIQUE ");
            sb.Append($"INDEX IF NOT EXISTS {Quote(index.Name)} ON {Quote(index.TableName)}");

            // RawMethod is the caller's explicit engine-specific choice and wins over the portable token.
            var method = !string.IsNullOrWhiteSpace(index.RawMethod)
                ? index.RawMethod
                : TranslateIndexMethod(index.Method);
            if (!string.IsNullOrWhiteSpace(method)) sb.Append($" USING {method}");

            var columns = index.Columns.Select(c =>
            {
                var part = Quote(c.Name);
                if (c.Descending) part += " DESC";
                if (c.Nulls == DbIndexNulls.First) part += " NULLS FIRST";
                else if (c.Nulls == DbIndexNulls.Last) part += " NULLS LAST";
                return part;
            });
            sb.Append($" ({string.Join(", ", columns)})");

            if (index.IncludeColumns.Count > 0)
                sb.Append($" INCLUDE ({string.Join(", ", index.IncludeColumns.Select(Quote))})");

            if (!string.IsNullOrWhiteSpace(index.Where))
                sb.Append($" WHERE {index.Where}");

            sb.Append(';');
            return sb.ToString();
        }

        /// <summary>
        /// Renders <c>DROP INDEX</c>. PostgreSQL identifies an index by name alone, but the owning table is
        /// carried on <see cref="DbIndex.TableName"/> for engines whose DROP requires it.
        /// </summary>
        private string GenerateDropIndex(string indexName) => $"DROP INDEX IF EXISTS {Quote(indexName)};";

        private string Quote(string id) => $"\"{id}\"";

        // Fallback name only used if a constraint somehow has no computed Name. Must be deterministic
        // (DbConstraint.Name already derives a stable name) so UP/DOWN scripts and regenerations agree.
        private string GuessConstraintName(DbConstraint con) => con.Name;

        public static readonly Dictionary<string, string> CSharpTypeMapping = new Dictionary<string, string>()
        {
            // Integers
          { "int", "integer" },
          { "int32", "integer" },
          { "long", "bigint" },
          { "int64", "bigint" },
          { "short", "smallint" },
          { "int16", "smallint" },
          { "byte", "smallint" },

          // Unsigned integers have no native PostgreSQL type, so the runtime widens them on read/write
          // (ushort->integer, uint->bigint, ulong->numeric, sbyte->smallint — see TableBindingsGenerator.MapPgType).
          // The DDL MUST match that widening or the column type diverges from what inserts/reads use. Without these
          // the fallback returned the raw .NET name ("uint32"), producing invalid CREATE TABLE DDL.
          { "sbyte", "smallint" },
          { "ushort", "integer" },
          { "uint16", "integer" },
          { "uint", "bigint" },
          { "uint32", "bigint" },
          { "ulong", "numeric" },
          { "uint64", "numeric" },

          // Decimals / Floats
          { "decimal", "numeric" }, // or "money" depending on use case
          { "double", "double precision" },
          { "float", "real" },
          { "single", "real" },

          // Strings / Text
          { "string", "text" }, // In Postgres, 'text' is preferred over varchar(max)
          { "char", "character(1)" },

          // Dates
          { "datetime", "timestamp without time zone" },
          { "datetimeoffset", "timestamp with time zone" },
          { "date", "date" },
          { "dateonly", "date" },
          { "time", "time without time zone" },
          { "timeonly", "time without time zone" },
          { "timespan", "interval" },

          // Booleans
          { "bool", "boolean" },
          { "boolean", "boolean" },

          // Special
          { "guid", "uuid" },
          { "byte[]", "bytea" },
          { "object", "jsonb" },

          // Namespace-qualified aliases — normalizes stale structure.json values
          // where GetDatabaseType previously fell through to the `return normalizedType` fallback.
          { "system.int16", "smallint" },
          { "system.int32", "integer" },
          { "system.int64", "bigint" },
          { "system.single", "real" },
          { "system.double", "double precision" },
          { "system.decimal", "numeric" },
          { "system.boolean", "boolean" },
          { "system.string", "text" },
          { "system.char", "character(1)" },
          { "system.datetime", "timestamp without time zone" },
          { "system.datetimeoffset", "timestamp with time zone" },
          { "system.dateonly", "date" },
          { "system.timeonly", "time without time zone" },
          { "system.timespan", "interval" },
          { "system.guid", "uuid" },
        };

        public string GetDatabaseType(string csharpType)
        {
            if (string.IsNullOrWhiteSpace(csharpType))
                return null;

            var normalizedType = csharpType.Trim().ToLower();

            if (CSharpTypeMapping.TryGetValue(normalizedType, out var dbType))
                return dbType;

            var parts = normalizedType.Split('.');
            if (CSharpTypeMapping.TryGetValue(parts[parts.Length - 1], out dbType))
                return dbType;

            return normalizedType;
        }
        #endregion
    }
}
