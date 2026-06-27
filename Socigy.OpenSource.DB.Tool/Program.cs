using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool;
using Socigy.OpenSource.DB.Tool.Introspection;
using Socigy.OpenSource.DB.Tool.Migrations;
using Socigy.OpenSource.DB.Tool.Scaffolding;
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

// ── scaffold: DB-first reverse engineering ────────────────────────────────

var schemaConnOpt = new Option<string>("--connection") { Required = true, Description = "PostgreSQL connection string (Npgsql format)." };
var schemaSchemaOpt = new Option<string>("--schema") { Description = "Database schema to read (default: public)." };
var schemaOutputOpt = new Option<FileInfo>("--output") { Description = "Output schema.json path (default: ./structure.json)." };

var scaffoldSchemaCommand = new Command("schema", "Generate a schema.json from a live database")
{
    schemaConnOpt, schemaSchemaOpt, schemaOutputOpt
};
scaffoldSchemaCommand.SetAction(ExecuteScaffoldSchemaAsync);

var classesConnOpt = new Option<string>("--connection") { Description = "PostgreSQL connection string (omit when using --from-schema)." };
var classesFromSchemaOpt = new Option<FileInfo>("--from-schema") { Description = "Read from an existing schema.json instead of a live database." };
var classesSchemaOpt = new Option<string>("--schema") { Description = "Database schema to read (default: public)." };
var classesOutputOpt = new Option<DirectoryInfo>("--output") { Required = true, Description = "Output directory for the generated classes." };
var classesNamespaceOpt = new Option<string>("--namespace") { Description = "Namespace for the generated classes (default: derived from the output directory)." };

var scaffoldClassesCommand = new Command("classes", "Generate annotated [Table] C# classes from a database or schema.json")
{
    classesConnOpt, classesFromSchemaOpt, classesSchemaOpt, classesOutputOpt, classesNamespaceOpt
};
scaffoldClassesCommand.SetAction(ExecuteScaffoldClassesAsync);

var scaffoldCommand = new Command("scaffold", "DB-first reverse engineering (schema.json and C# classes)")
{
    scaffoldSchemaCommand, scaffoldClassesCommand
};

var root = new RootCommand("Socigy.OpenSource.DB Model/Migration Generation Tool")
{
    generateCommand,
    scaffoldCommand
};

var result = root.Parse(args);
return await result.InvokeAsync();

async Task<int> ExecuteScaffoldSchemaAsync(ParseResult result, CancellationToken cancellationToken)
{
    string connection = result.GetValue(schemaConnOpt)!;
    string schema = result.GetValue(schemaSchemaOpt) ?? "public";
    FileInfo? output = result.GetValue(schemaOutputOpt);
    string outputPath = output?.FullName ?? Path.Combine(Directory.GetCurrentDirectory(), "structure.json");

    Logger.Log($"Reading schema '{schema}' from database...");
    DbSchema dbSchema = await PostgresSchemaReader.ReadAsync(connection, schema);

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    await using var stream = File.Create(outputPath);
    await JsonSerializer.SerializeAsync(stream, dbSchema, Configuration.JsonOptions);
    Logger.Log($"Wrote {dbSchema.Tables.Count} table(s) to {outputPath}");
    return 0;
}

async Task<int> ExecuteScaffoldClassesAsync(ParseResult result, CancellationToken cancellationToken)
{
    string? connection = result.GetValue(classesConnOpt);
    FileInfo? fromSchema = result.GetValue(classesFromSchemaOpt);
    string schema = result.GetValue(classesSchemaOpt) ?? "public";
    DirectoryInfo outputDir = result.GetValue(classesOutputOpt)!;
    string ns = result.GetValue(classesNamespaceOpt) ?? Naming.ToPascalCase(outputDir.Name);

    if (string.IsNullOrEmpty(connection) && fromSchema == null)
    {
        Logger.Error("Provide either --connection (live database) or --from-schema (existing schema.json).");
        return 1;
    }

    DbSchema dbSchema;
    if (fromSchema != null)
    {
        if (!fromSchema.Exists)
        {
            Logger.Error($"schema.json not found: {fromSchema.FullName}");
            return 1;
        }
        await using var input = fromSchema.OpenRead();
        dbSchema = (await JsonSerializer.DeserializeAsync<DbSchema>(input, Configuration.JsonOptions))
            ?? throw new InvalidDataException($"Could not read schema from {fromSchema.FullName}");
    }
    else
    {
        Logger.Log($"Reading schema '{schema}' from database...");
        dbSchema = await PostgresSchemaReader.ReadAsync(connection!, schema);
    }

    outputDir.Create();
    var files = CSharpClassEmitter.Emit(dbSchema, ns);
    foreach (var (fileName, content) in files)
        await File.WriteAllTextAsync(Path.Combine(outputDir.FullName, fileName), content);

    Logger.Log($"Generated {files.Count} class(es) in namespace '{ns}' at {outputDir.FullName}");
    return 0;
}

async Task<int> ExecuteGenerateAsync(ParseResult result, CancellationToken cancellationToken)
{
    FileInfo assemblyPath = result.GetValue(targetAssemblyOpt)!;
    bool shouldMigrate = result.GetValue(migrateOpt);
    DirectoryInfo projectDir = result.GetValue(projectDirOpt)!;

    if (Path.GetFileNameWithoutExtension(assemblyPath.FullName).Contains("Socigy.OpenSource.DB"))
        return 0; // building the library itself: nothing to generate, not an error

    if (!assemblyPath.Exists)
    {
        Logger.Error($"Assembly not found: {assemblyPath.FullName}");
        return 1;
    }

    if (shouldMigrate)
        Logger.Warning($"Will generate DB migration script!");

    await Configuration.InitializeAsync(projectDir.FullName, assemblyPath);

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

    bool isFirstMigration = Configuration.SavedSchema == null;
    var diff = SchemaComparer.Compare(isFirstMigration ? new DbSchema() : Configuration.SavedSchema!, Configuration.CurrentSchema);
    string diffPath = Configuration.StructureDiffJsonPath;
    if (File.Exists(diffPath))
        File.Delete(diffPath);

    diff.ClearOutEmpty();
    stream = File.OpenWrite(diffPath);
    await JsonSerializer.SerializeAsync<SchemaDiff>(stream, diff, Configuration.JsonOptions);
    await stream.DisposeAsync();

    if (shouldMigrate)
        await MigrationGenerator.PublishMigration(diff, isFirstMigration);

    Logger.Log($"Finished tasks in {watch.ElapsedMilliseconds}ms");
    watch.Stop();
    return 0;
}