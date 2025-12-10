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
        public static async Task PublishMigration(SchemaDiff diff)
        {
            diff.ProvideDefaults();

            if (diff.IsEmpty)
            {
#if IsWindows
                MessageBox.Show("Current DB Schema is the same as the saved schema, no need to create migration script.\r\n\r\nAborting!", $"{Configuration.BaseNamespace}: Migration script generation was aborted", MessageBoxButtons.OK);
#else
                Logger.Warning($"{Configuration.BaseNamespace}: Current DB Schema is the same as the saved schema, no need to create migration script. Aborting!");
#endif
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

            var (upScript, downScript) = sqlGenerator.Generate(diff);

#if IsWindows
            string? migrationName = UI.MigrationNameInputDialog.Show($"{Configuration.BaseNamespace}: Please choose name for the new DB migration", "DB Migration Name:");
            if (migrationName == null)
            {
                Logger.Error("User canceled the migration creation process!");
                Environment.Exit(-1);
            }

            migrationName = Configuration.Settings.Database.MigrationNameTemplate.Replace("${Name}", migrationName).Replace("${D}", MigrationNamer.GetMigrationId());
#else

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
    }
}
