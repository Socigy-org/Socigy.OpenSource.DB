using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using Socigy.OpenSource.DB.Tool.Templates;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Socigy.OpenSource.DB.Tool
{
    internal static class Configuration
    {
        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = JsonTypeInfoResolver.Combine(StructuresJsonContext.Default),
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };

        public static SocigySettings Settings { get; private set; }
        public static DbSchema? SavedSchema { get; private set; }
        public static DbSchema? CurrentSchema { get; set; }
        public static string ProjectDir { get; private set; }

        public static string SocigyProjectFolderPath => $"{ProjectDir}\\Socigy\\";
        public static string SocigyMigrationsFolderPath => $"{ProjectDir}\\Socigy\\Migrations\\";
        public static string StructureJsonPath => $"{SocigyProjectFolderPath}structure.json";
        public static string StructureBackupJsonPath => $"{SocigyProjectFolderPath}structure.backup.json";
        public static string StructureDiffJsonPath => $"{SocigyProjectFolderPath}diff.json";
        public static string StructureCurrentJsonPath => $"{SocigyProjectFolderPath}structure.dirty.json";
        public static string SocigyGitignoreFilePath => $"{SocigyProjectFolderPath}.gitignore";
        public static string SocigyConfigFilePath => $"{ProjectDir}\\socigy.json";

        public static string BaseNamespace { get; set; }
        public static async Task InitializeAsync(string projectDir)
        {
            ProjectDir = projectDir;

            if (!Directory.Exists(SocigyProjectFolderPath))
                Directory.CreateDirectory(SocigyProjectFolderPath);

            if (!File.Exists(SocigyGitignoreFilePath))
                await File.WriteAllTextAsync(SocigyGitignoreFilePath, new GitignoreTemplate().TransformText());

            if (!File.Exists(SocigyConfigFilePath))
            {
                Logger.Warning($"No configuration file found at '{SocigyConfigFilePath}'. Using/Saving defaults!");
                Settings = new SocigySettings()
                {
                    Database = new DatabaseSettings()
                    {
                        Platform = "postgresql"
                    }
                };
                var stream = File.OpenWrite(SocigyConfigFilePath);
                await JsonSerializer.SerializeAsync(stream, Settings, JsonOptions);
                await stream.DisposeAsync();
            }
            else
            {
                try
                {
                    var stream = File.OpenRead(SocigyConfigFilePath);
                    Settings = (await JsonSerializer.DeserializeAsync<SocigySettings>(stream, JsonOptions))!;
                    await stream.DisposeAsync();
                }
                catch { }
                if (Settings == null)
                {
                    Logger.Error("Invalid configuration in socigy.json, please fix! Aborting...");
                    throw new InvalidDataException($"Invalid configuration loaded from {SocigyConfigFilePath}");
                }
            }

            if (File.Exists(StructureJsonPath))
            {
                try
                {
                    var stream = File.OpenRead(StructureJsonPath);
                    SavedSchema = (await JsonSerializer.DeserializeAsync<DbSchema>(stream, JsonOptions))!;
                    await stream.DisposeAsync();
                }
                catch
                {
                    Logger.Error("Corrupted structure.json found, please fix! Aborting...");
                    throw new InvalidDataException($"Corrupted structure.json at {StructureJsonPath}");
                }
            }
        }

        public static ISqlGenerator? GetSqlGenerator()
        {
            switch (Settings.Database.Platform)
            {
                case "postgres":
                case "npgsql":
                case "postgre":
                case "postgresql":
                    return new PostgreSqlGenerator();

                default:
                    return default;
            }
        }
    }
}
