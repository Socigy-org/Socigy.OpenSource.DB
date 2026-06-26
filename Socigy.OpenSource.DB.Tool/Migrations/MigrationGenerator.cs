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

            File.Delete(Configuration.StructureBackupJsonPath);
            if (File.Exists(Configuration.StructureJsonPath))
                File.Move(Configuration.StructureJsonPath, Configuration.StructureBackupJsonPath);

            Configuration.CurrentSchema!.Id = migrationName;
            await File.WriteAllTextAsync(Configuration.StructureJsonPath, JsonSerializer.Serialize(Configuration.CurrentSchema, Configuration.JsonOptions));
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
