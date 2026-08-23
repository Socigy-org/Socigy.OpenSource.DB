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

            // Refuse to fork the chain. The new migration's parent is the saved snapshot's id, and the
            // snapshot only advances after the file is written — so an abandoned attempt at the same change
            // still claims that parent, and the fresh timestamp in the filename means the tool cannot
            // overwrite its own prior emission either. Caught here, this is a message naming the file; caught
            // at apply time (the only place it used to be caught) it takes the module's whole schema offline.
            var chainConflicts = MigrationChainInspector.DetectConflicts(
                MigrationChainInspector.Read(Configuration.SocigyMigrationsFolderPath),
                Configuration.SavedSchema?.Id);

            if (chainConflicts.Count > 0)
            {
                Logger.Error($"{Configuration.BaseNamespace}: Generating a migration here would leave the migration chain unapplicable:");
                foreach (var conflict in chainConflicts)
                    Logger.Error($"  - {conflict}");
                Logger.Error("Nothing was written.");
                Environment.Exit(-1);
            }

            var (upScript, downScript) = sqlGenerator.Generate(diff, firstMigration);

            // A blocking issue means there is no correct SQL for the change — not that the SQL is risky.
            // Abort BEFORE writing anything: no .g.cs, and no advance of the schema snapshot, so re-running
            // after the model is corrected produces exactly one migration rather than a forked chain.
            if (sqlGenerator.BlockingIssues.Count > 0)
            {
                Logger.Error($"{Configuration.BaseNamespace}: This change cannot be expressed as a migration, so none was generated:");
                foreach (var issue in sqlGenerator.BlockingIssues)
                    Logger.Error($"  - {issue}");
                Logger.Error("Nothing was written. Adjust the model, or hand-author the migration described above.");
                Environment.Exit(-1);
            }

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
            // so structure.json is never missing and is never left half-written.
            //
            // The .g.cs is still written first on purpose: advancing the snapshot without a migration file
            // silently loses the change, which is unrecoverable, whereas a crash here leaves a file whose
            // parent the next run would also claim. That duplicate is precisely a fork — not the "recoverable"
            // outcome an earlier version of this comment claimed, since recovering it means knowing to look —
            // so MigrationChainInspector above refuses the next run rather than leaving it to apply time.
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
