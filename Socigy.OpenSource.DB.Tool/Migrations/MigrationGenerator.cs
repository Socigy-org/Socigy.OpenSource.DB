using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using Socigy.OpenSource.DB.Tool.Templates;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Socigy.OpenSource.DB.Tool.Migrations
{
    public static class MigrationGenerator
    {
        public static async Task PublishMigration(SchemaDiff diff, bool firstMigration)
        {
            diff.ProvideDefaults();

            if (diff.IsEmpty)
            {
#if IsWindows
                if (Configuration.Settings.ShouldShowMessageOnEmptyMigrationGeneration)
                {
                    RunOnStaThread(() => MessageBox.Show("Current DB Schema is the same as the saved schema, no need to create migration script.\r\n\r\nAborting!", $"{Configuration.BaseNamespace}: Migration script generation was aborted", MessageBoxButtons.OK));
                }
#endif
                Logger.Warning($"{Configuration.BaseNamespace}: Current DB Schema is the same as the saved schema, no need to create migration script. Aborting!");
                Environment.Exit(0);
            }

            var sqlGenerator = Configuration.GetSqlGenerator();
            if (sqlGenerator == null)
            {
                Logger.Error("No valid DB platform is selected. Please configure your DB platform in socigy.json and make sure it's a valid");
                Environment.Exit(-1);
            }

            if (!Directory.Exists(Configuration.SocigyMigrationsFolderPath))
                Directory.CreateDirectory(Configuration.SocigyMigrationsFolderPath);

            var (upScript, downScript) = sqlGenerator.Generate(diff, firstMigration);

            if (sqlGenerator.DestructiveOperations.Count > 0)
            {
                Logger.Warning($"{Configuration.BaseNamespace}: This migration contains DESTRUCTIVE, data-losing operations:");
                foreach (var op in sqlGenerator.DestructiveOperations)
                    Logger.Warning($"  - {op}");
                Logger.Warning("Review the generated migration (search for [SOCIGY:DESTRUCTIVE]) before applying it.");
            }

            if (sqlGenerator.SafetyWarnings.Count > 0)
            {
                Logger.Warning($"{Configuration.BaseNamespace}: This migration has potential safety issues to review before applying:");
                foreach (var w in sqlGenerator.SafetyWarnings)
                    Logger.Warning($"  - {w}");
            }

#if IsWindows
            string? migrationName = null;
            RunOnStaThread(() => migrationName = UI.MigrationNameInputDialog.Show($"{Configuration.BaseNamespace}: Please choose name for the new DB migration", "DB Migration Name:"));
            if (migrationName == null)
            {
                Logger.Error("User canceled the migration creation process!");
                Environment.Exit(-1);
            }

            migrationName = Configuration.Settings.Database.MigrationNameTemplate.Replace("${Name}", migrationName).Replace("${Timestamp}", MigrationNamer.GetMigrationId());
            var formattedMigrationName = migrationName.Replace(" ", "_");
            migrationName = MigrationNamer.GenerateUniqueName(formattedMigrationName);
#else
            // Headless (non-Windows) build: there is no interactive name dialog. GenerateCanonicalString
            // contains newlines and ':' separators, so it must NOT be used as a file/class name — derive a
            // clean, deterministic identifier from the diff instead (valid C# identifier + filename).
            string migrationName = MigrationNamer.GenerateUniqueName(diff);
#endif
            await File.WriteAllTextAsync($"{Configuration.SocigyMigrationsFolderPath}{migrationName}.g.cs", new MigrationFileTemplate()
            {
                Id = migrationName,
                Name = $"M_{migrationName}",
                BaseNamespace = $"{Configuration.BaseNamespace}.Socigy.Migrations",

                UpSql = String.Join(Environment.NewLine, upScript),
                DownSql = String.Join(Environment.NewLine, downScript),
                PreviousId = Configuration.SavedSchema?.Id
            }.TransformText());

            // Advance the schema snapshot atomically. The previous code moved structure.json to the backup and
            // THEN wrote the new one, leaving a window with no structure.json at all — a crash there made the next
            // run see no saved schema and re-emit every migration. Instead: write the new snapshot to a temp file,
            // copy the prior snapshot to the backup, then move the temp into place (an atomic same-volume rename),
            // so structure.json is never missing and is never left half-written. (The .g.cs is written first on
            // purpose: a crash before this point yields at worst a duplicate migration on the next run — recoverable
            // — rather than advancing the snapshot without a migration file, which would silently lose the change.)
            Configuration.CurrentSchema!.Id = migrationName;
            var newSnapshotJson = JsonSerializer.Serialize(Configuration.CurrentSchema, Configuration.JsonOptions);
            var tempSnapshotPath = Configuration.StructureJsonPath + ".tmp";
            await File.WriteAllTextAsync(tempSnapshotPath, newSnapshotJson);
            if (File.Exists(Configuration.StructureJsonPath))
                File.Copy(Configuration.StructureJsonPath, Configuration.StructureBackupJsonPath, overwrite: true);
            File.Move(tempSnapshotPath, Configuration.StructureJsonPath, overwrite: true);
        }

#if IsWindows
        // WinForms ShowDialog / MessageBox require an STA thread.
        // async continuations run on ThreadPool (MTA), so we marshal UI calls explicitly.
        private static void RunOnStaThread(Action action)
        {
            Exception? caught = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { caught = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (caught != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(caught).Throw();
        }
#endif
    }
}
