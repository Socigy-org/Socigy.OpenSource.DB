using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Socigy.OpenSource.DB.SourceGenerator.Templates;
using Socigy.OpenSource.DB.SourceGenerator.Templates.CommandBuilders;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Socigy.OpenSource.DB.SourceGenerator
{
    public static class MigrationGenerator
    {
        public static void Execute(SourceProductionContext ctx, Compilation compilation, Program program)
        {
            // No platform/socigy.json -> nothing to generate. Guard like ContextGenerator/ExtensionGenerator so
            // the generator no-ops (instead of feeding a null DatabasePrefix into a template, which throws
            // ArgumentNullException('objectToConvert') in ToStringWithCulture) on consumer projects with no config.
            if (string.IsNullOrWhiteSpace(program.DatabasePrefix))
                return;

            // Identifier base (the generated `partial class {dbName}` holder etc.) — uses the type name so a
            // lowercase databaseName doesn't produce an all-lowercase type name (CS8981).
            string dbName = program.DatabaseTypeName;

            var migrationTableNamespace = $"{compilation.AssemblyName}.Socigy.Generated";
            ctx.AddSource("Migrations.g.cs", new MigrationTableTemplate()
            {
                BaseNamespace = migrationTableNamespace,
                DbName = dbName
            }.TransformText());
            ctx.AddSource("Migrations.table.g.cs", new TableColumnNameClassTemplate()
            {
                ClassName = "Migration",
                Namespace = migrationTableNamespace,
                TableName = "_scg_migrations",
                CustomPreClass = $"public static partial class {dbName}\n{{",
                CustomPostClass = "}",
                Columns = [
                    new TableColumnNameClassTemplate.ColumnInfo() { Name = "Id", DatabaseName = "id", Type = typeof(long).FullName, IsPrimaryKey = true, IsAutoIncrement = true, SequenceName = "_scg_migrations_id_seq" },
                    new TableColumnNameClassTemplate.ColumnInfo() { Name = "HumanId", DatabaseName = "human_id",  Type = typeof(string).FullName },
                    new TableColumnNameClassTemplate.ColumnInfo() { Name = "IsRollback", DatabaseName = "is_rollback",  Type = typeof(bool).FullName },
                    new TableColumnNameClassTemplate.ColumnInfo() { Name = "AppliedAt", DatabaseName = "applied_at" , Type = typeof(DateTime).FullName },
                    new TableColumnNameClassTemplate.ColumnInfo() { Name = "ExecutedBy", DatabaseName = "executed_by" , Type = typeof(string).FullName },
                ],
            }.TransformText());
            ctx.AddSource("MigrationsSyntaxMethods.table.g.cs", new TableSyntaxGeneratorTemplate()
            {
                ClassName = "Migration",
                Namespace = migrationTableNamespace,
                DbEnginePrefix = program.DatabasePrefix,
                CustomPreClass = $"public static partial class {dbName}\n{{",
                CustomPostClass = "}",
                Columns =
                [
                    ("Id", typeof(long).FullName, true, null, true, "_scg_migrations_id_seq", false, null, false, true, null),
                    ("HumanId", typeof(string).FullName, false, null, false, null, false, null, false, true, null),
                    ("IsRollback", typeof(bool).FullName, false, null, false, null, false, null, false, true, null),
                    ("AppliedAt", typeof(DateTime).FullName, false, null, false, null, false, null, false, true, null),
                    ("ExecutedBy", typeof(string).FullName, false, null, false, null, false, null, false, true, null),
                ],
            }.TransformText());

            var updateBuilderTemplate = new PostgresqlUpdateCommandBuilder()
            {
                ClassName = "Migration",
                Namespace = migrationTableNamespace,
                CustomPreClass = $"using static {migrationTableNamespace}.{dbName};",
                CustomPostClass = string.Empty
            };
            ctx.AddSource($"Migration.builder.update.g.cs", updateBuilderTemplate.TransformText());

            var deleteBuilderTemplate = new PostgresqlDeleteCommandBuilder()
            {
                ClassName = "Migration",
                Namespace = migrationTableNamespace,
                CustomPreClass = $"using static {migrationTableNamespace}.{dbName};",
                CustomPostClass = string.Empty
            };
            ctx.AddSource($"Migration.builder.delete.g.cs", deleteBuilderTemplate.TransformText());


            var migrationManager = new MigrationManagerTemplate()
            {
                BaseNamespace = migrationTableNamespace,
                DatabaseName = dbName,
                // Raw databaseName for the [FromKeyedServices(...)] connection-factory lookup (matches the keyed
                // registration in the DI extension); separate from dbName which is the C# type-name base.
                ServiceKey = program.Settings?.Database?.DatabaseName ?? "UnnamedDb",
                MigrationClassNames = []
            };
            foreach (var migration in program.LocalMigrations)
            {
                var semanticModel = compilation.GetSemanticModel(migration.SyntaxTree);
                if (semanticModel.GetDeclaredSymbol(migration) is not INamedTypeSymbol semantics)
                    continue;

                var localMigration = semanticModel.Compilation.GetTypeByMetadataName(Program.ILocalMigrationFullName);

                if (semantics.AllInterfaces.Any(x => SymbolEqualityComparer.Default.Equals(x, localMigration)))
                    migrationManager.MigrationClassNames.Add(semantics.ToDisplayString());
            }

            ctx.AddSource("MigrationManager.g.cs", migrationManager.TransformText());
        }
    }
}
