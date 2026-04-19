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
            string dbName = program.Settings?.Database.DatabaseName ?? "UnnamedDb";

            // Migrations table, needed everytime
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
                    ("Id", typeof(long).FullName, true, null, true, "_scg_migrations_id_seq"),
                    ("HumanId", typeof(string).FullName, false, null, false, null),
                    ("IsRollback", typeof(bool).FullName, false, null, false, null),
                    ("AppliedAt", typeof(DateTime).FullName, false, null, false, null),
                    ("ExecutedBy", typeof(string).FullName, false, null, false, null),
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
