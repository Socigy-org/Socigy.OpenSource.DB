using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool;
using Socigy.OpenSource.DB.Tool.Migrations;
using Socigy.OpenSource.DB.Tool.Structures;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

var targetAssemblyOpt = new Option<FileInfo>("--target-assembly")
{
    Required = true,
    Description = "The path to the target assembly."
};
var migrateOpt = new Option<bool>("--migrate")
{
    Description = "Indicates if the DB migration class should be generated."
};
var projectDirOpt = new Option<DirectoryInfo>("--project-dir")
{
    Required = true,
    Description = "The directory of the target project."
};

var generateCommand = new Command("generate", "Generates DB model/migration files")
{
    targetAssemblyOpt,
    migrateOpt,
    projectDirOpt
};

generateCommand.SetAction(ExecuteGenerateAsync);

var root = new RootCommand("Socigy.OpenSource.DB Model/Migration Generation Tool")
{
    generateCommand
};

// TODO: Command for generating schema.json from DB connection
// TODO: Command for generating C# classes from DB connection

var result = root.Parse(args);
return await result.InvokeAsync();

async Task ExecuteGenerateAsync(ParseResult result)
{
    FileInfo assemblyPath = result.GetValue(targetAssemblyOpt)!;
    bool shouldMigrate = result.GetValue(migrateOpt);
    DirectoryInfo projectDir = result.GetValue(projectDirOpt)!;

    if (!assemblyPath.Exists)
    {
        Logger.Error($"Assembly not found: {assemblyPath.FullName}");
        return;
    }

    if (shouldMigrate)
        Logger.Warning($"Will generate DB migration script!");

    await Configuration.InitializeAsync(projectDir.FullName);

    Stopwatch watch = Stopwatch.StartNew();
    var schema = AssemblyAnalyzer.LoadAndAnalyze(assemblyPath);
    Configuration.CurrentSchema = schema;
    Configuration.CurrentSchema.PreviousId = Configuration.SavedSchema?.Id;

    string currentSchemaJson = Configuration.StructureCurrentJsonPath;
    if (File.Exists(currentSchemaJson))
        File.Delete(currentSchemaJson);

    var stream = File.OpenWrite(currentSchemaJson);
    await JsonSerializer.SerializeAsync(stream, schema, Configuration.JsonOptions);
    await stream.DisposeAsync();

    var diff = SchemaComparer.Compare(Configuration.SavedSchema ?? new DbSchema(), Configuration.CurrentSchema);
    string diffPath = Configuration.StructureDiffJsonPath;
    if (File.Exists(diffPath))
        File.Delete(diffPath);

    diff.ClearOutEmpty();
    stream = File.OpenWrite(diffPath);
    await JsonSerializer.SerializeAsync<SchemaDiff>(stream, diff, Configuration.JsonOptions);
    await stream.DisposeAsync();

    if (shouldMigrate)
        await MigrationGenerator.PublishMigration(diff);

    Logger.Log($"Finished tasks in {watch.ElapsedMilliseconds}ms");
    watch.Stop();
}