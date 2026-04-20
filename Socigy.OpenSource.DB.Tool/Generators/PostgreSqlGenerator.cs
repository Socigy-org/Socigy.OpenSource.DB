using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Socigy.OpenSource.DB.Tool.Generators
{
    public class PostgreSqlGenerator : ISqlGenerator
    {
        public (IEnumerable<string> Up, IEnumerable<string> Down) Generate(SchemaDiff diff, bool isFirstMigration)
        {
            var upCommands = new List<string>();
            var downCommands = new List<string>();

            // --- UP: 1. Drop Removed Tables ---
            // --- DOWN: 5. Re-Create Removed Tables & Restore Data ---
            foreach (var table in diff.RemovedTables)
            {
                upCommands.Add($"DROP TABLE IF EXISTS {Quote(table.Name)} CASCADE;");

                // Down: Recreate Schema
                downCommands.Add(GenerateCreateTable(table));

                // Down: Restore Data (InstantiatedValues)
                if (table.InstantiatedValues != null && table.InstantiatedValues.Any())
                {
                    foreach (var row in table.InstantiatedValues)
                    {
                        downCommands.Add(GenerateInsertStatement(table.Name, row));
                    }
                }
            }

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

                downCommands.Insert(0, $"DROP TABLE IF EXISTS {Quote(table.Name)} CASCADE;");

                // Drop sequences after table is gone
                foreach (var seqDown in GenerateDropSequences(table))
                    downCommands.Insert(0, seqDown);
            }

            // --- UP: 4. Alter Tables (Schema & Data) ---
            // --- DOWN: 2. Revert Alterations ---
            foreach (var alteration in diff.AlteredTables)
            {
                // A. Schema Changes
                var (schemaUps, schemaDowns) = GenerateTableAlterations(alteration);
                upCommands.AddRange(schemaUps);

                foreach (var cmd in ((IEnumerable<string>)schemaDowns).Reverse())
                {
                    downCommands.Insert(0, cmd);
                }

                // B. Data Changes (Rows Added/Removed/Modified)
                var (dataUps, dataDowns) = GenerateDataAlterations(alteration);
                upCommands.AddRange(dataUps);

                // Prepend data rollbacks so they happen before schema rollbacks
                foreach (var cmd in ((IEnumerable<string>)dataDowns).Reverse())
                {
                    downCommands.Insert(0, cmd);
                }
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

            return (upCommands, downCommands);
        }

        private string GenerateCreateTable(DbTable table)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"CREATE TABLE {Quote(table.Name)} (");

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

            // C. Primary Keys (Aggregated from Columns)
            var pkColumns = table.Columns.Where(c => c.IsPrimaryKey == true).ToList();
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

            var tableName = Quote(alteration.Table.Name);

            // 1. Removed Constraints
            foreach (var c in alteration.RemovedConstraints)
            {
                up.Add($"ALTER TABLE {tableName} DROP CONSTRAINT IF EXISTS {Quote(c.Name)};");
                down.Add(GenerateAddConstraint(alteration.Table.Name, c));
            }

            // 2. Removed Columns
            foreach (var c in alteration.RemovedColumns)
            {
                up.Add($"ALTER TABLE {tableName} DROP COLUMN {Quote(c.Name)};");
                down.Add($"ALTER TABLE {tableName} ADD COLUMN {GenerateColumnDefinition(c)};");
            }

            // 3. Added Columns (create sequences for new auto-increment columns first)
            foreach (var c in alteration.AddedColumns)
            {
                if (c.IsAutoIncrement == true)
                {
                    var seqName = GetSequenceName(alteration.Table.Name, c);
                    up.Add($"CREATE SEQUENCE IF NOT EXISTS {Quote(seqName)};");
                    down.Add($"DROP SEQUENCE IF EXISTS {Quote(seqName)};");
                }
                up.Add($"ALTER TABLE {tableName} ADD COLUMN {GenerateColumnDefinition(c)};");
                down.Add($"ALTER TABLE {tableName} DROP COLUMN {Quote(c.Name)};");
            }

            // 4. Renamed Columns
            foreach (var renaming in alteration.RenamedColumns)
            {
                up.Add($"ALTER TABLE {tableName} RENAME COLUMN {Quote(renaming.Old.Name)} TO {Quote(renaming.New.Name)};");
                down.Add($"ALTER TABLE {tableName} RENAME COLUMN {Quote(renaming.New.Name)} TO {Quote(renaming.Old.Name)};");
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
                            up.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} TYPE {newType} USING {colName}::{newType};");
                            var oldType = mod.OldColumn.DatabaseType;
                            down.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} TYPE {oldType} USING {colName}::{oldType};");
                            break;

                        case "Nullable":
                            var upAction = mod.NewColumn.Nullable == true ? "DROP NOT NULL" : "SET NOT NULL";
                            up.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} {upAction};");
                            var downAction = mod.OldColumn.Nullable == true ? "DROP NOT NULL" : "SET NOT NULL";
                            down.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} {downAction};");
                            break;

                        case "Default":
                            if (string.IsNullOrEmpty(mod.NewColumn.DefaultValue))
                                up.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} DROP DEFAULT;");
                            else
                                up.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} SET DEFAULT {mod.NewColumn.DefaultValue};");

                            if (string.IsNullOrEmpty(mod.OldColumn.DefaultValue))
                                down.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} DROP DEFAULT;");
                            else
                                down.Add($"ALTER TABLE {tableName} ALTER COLUMN {colName} SET DEFAULT {mod.OldColumn.DefaultValue};");
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
            }

            // 7. Added Constraints
            foreach (var c in alteration.AddedConstraints)
            {
                up.Add(GenerateAddConstraint(alteration.Table.Name, c));
                down.Add($"ALTER TABLE {tableName} DROP CONSTRAINT IF EXISTS {Quote(c.Name)};");
            }

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
                // Fallback: If no PK, match ALL columns (safest best effort)
                foreach (var kvp in row)
                {
                    criteria.Add($"{Quote(kvp.Key)} = {FormatSqlValue(kvp.Value)}");
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
                default:
                    // Numbers, etc.
                    if (double.TryParse(value.ToString(), out _))
                        return Convert.ToString(value, CultureInfo.InvariantCulture);

                    // Fallback to string representation
                    return $"'{value.ToString().Replace("'", "''")}'";
            }
        }

        private string GenerateColumnDefinition(DbColumn col)
        {
            var sb = new StringBuilder();
            sb.Append($"{Quote(col.Name)} {col.DatabaseType}");
            if (col.Nullable == false) sb.Append(" NOT NULL");

            if (col.IsAutoIncrement == true)
            {
                var seqName = GetSequenceName(null, col);
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
            if (col.Nullable == false) sb.Append(" NOT NULL");

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

        private IEnumerable<string> GenerateDropSequences(DbTable table)
        {
            foreach (var col in table.Columns.Where(c => c.IsAutoIncrement == true))
            {
                yield return $"DROP SEQUENCE IF EXISTS {Quote(GetSequenceName(table.Name, col))};";
            }
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
                    var uniqueCols = string.Join(", ", con.Columns.Select(x => Quote(sourceTable?.Columns.FirstOrDefault(y => y.SourceName != null && y.SourceName.Split('.').Last() == x)?.Name ?? x)));
                    sb.Append($"UNIQUE ({uniqueCols})");
                    break;
                case "check":
                    sb.Append($"CHECK ({con.Value})");
                    break;
                case "foreign_key":
                    var fkCols = string.Join(", ", con.Columns.Select(x => Quote(sourceTable?.Columns.FirstOrDefault(y => y.SourceName != null && y.SourceName.Split('.').Last() == x)?.Name ?? x)));
                    var targetTable = Configuration.CurrentSchema.Tables.FirstOrDefault(x => x.SourceName == con.TargetTable);
                    var targetTableName = targetTable?.Name ?? con.TargetTable;
                    var targetCols = string.Join(", ", con.TargetColumns.Select(x =>
                        Quote(targetTable?.Columns.FirstOrDefault(y => y.SourceName != null && y.SourceName.Split('.').Last() == x)?.Name ?? x)));
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

        private string Quote(string id) => $"\"{id}\"";

        private string GuessConstraintName(DbConstraint con) => $"IX_{Guid.NewGuid().ToString("N").Substring(0, 8)}";

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
          { "time", "time without time zone" },
          { "timespan", "interval" },

          // Booleans
          { "bool", "boolean" },
          { "boolean", "boolean" },

          // Special
          { "guid", "uuid" },
          { "byte[]", "bytea" },
          { "object", "jsonb" }
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
