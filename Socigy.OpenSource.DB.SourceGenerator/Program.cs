using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Core.Settings;
using Socigy.OpenSource.DB.Migrations;
using Socigy.OpenSource.DB.SourceGenerator.Templates;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Socigy.OpenSource.DB.SourceGenerator
{
    // TODO: Proper SqlCommand/Connection disposals: await using

    [Generator]
    public class Program : IIncrementalGenerator
    {
        public SocigySettings? Settings { get; set; }
        public string? DatabasePrefix { get; set; }
        public ImmutableArray<ClassDeclarationSyntax> LocalMigrations { get; set; }

        public static readonly string TableAttributeFullName = typeof(TableAttribute).FullName;
        public static readonly string ILocalMigrationFullName = typeof(ILocalMigration).FullName;
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            //Debugger.Launch();
            var settingsText = context.AdditionalTextsProvider
                .Where(x => Path.GetFileName(x.Path) == "socigy.json")
                .Select((text, cancellationToken) => text.GetText(cancellationToken)?.ToString());

            IncrementalValuesProvider<ClassDeclarationSyntax> tableClasses =
                 context.SyntaxProvider
                         .ForAttributeWithMetadataName(
                             TableAttributeFullName,
                             static (node, _) => node is ClassDeclarationSyntax,
                             static (ctx, _) =>
                             {
                                 if (ctx.TargetNode is not ClassDeclarationSyntax classSyntax)
                                     return null;

                                 if (ctx.SemanticModel.GetDeclaredSymbol(classSyntax) is not INamedTypeSymbol semantics)
                                     return null;

                                 var tableAttribute = ctx.SemanticModel.Compilation.GetTypeByMetadataName(TableAttributeFullName);
                                 return semantics.GetAttributes().Any(x => SymbolEqualityComparer.Default.Equals(x.AttributeClass, tableAttribute))
                                    ? classSyntax
                                    : null;
                             })
                     .Where(x => x != null)!;

            IncrementalValuesProvider<ClassDeclarationSyntax> migrationClasses =
                context.SyntaxProvider.CreateSyntaxProvider(
                        predicate: static (node, _) =>
                            node is ClassDeclarationSyntax c && c.BaseList != null,

                        transform: static (ctx, _) =>
                        {
                            var classSyntax = (ClassDeclarationSyntax)ctx.Node;

                            if (ctx.SemanticModel.GetDeclaredSymbol(classSyntax) is not INamedTypeSymbol classSymbol)
                                return null;

                            var localMigration = ctx.SemanticModel.Compilation.GetTypeByMetadataName(ILocalMigrationFullName);

                            return localMigration != null &&
                                classSymbol.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, localMigration)) ? classSyntax : null;
                        })
                    .Where(x => x != null)!;

            context.RegisterSourceOutput(settingsText, (ctx, settingsRaw) =>
            {
                if (settingsRaw == null)
                {
                    Settings = new();
                    DatabasePrefix = GetDatabasePrefix();
                }
                else
                {
                    Settings = JsonSerializer.Deserialize<SocigySettings>(settingsRaw, new JsonSerializerOptions()
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });

                    DatabasePrefix = GetDatabasePrefix();
                }
            });

            context.RegisterSourceOutput(migrationClasses.Collect(), (ctx, migrations) =>
            {
                LocalMigrations = migrations;
            });

            context.RegisterSourceOutput(context.CompilationProvider.Combine(tableClasses.Collect()), Execute);
        }

        public void Execute(SourceProductionContext ctx, (Compilation, ImmutableArray<ClassDeclarationSyntax>) tuple)
        {
            var (compilation, tables) = tuple;

            if (compilation.AssemblyName!.StartsWith("Socigy.OpenSource.DB"))
                return; // Skip self-generation

            // Table.Query() and other method generation
            TableBindingsGenerator.Execute(ctx, compilation, tables, this);

            // IServiceProvider and WebApplicationBuilder extensions
            ExtensionGenerator.Execute(ctx, compilation, this);

            // [Table("_scg_migrations")]
            // MigrationManager bindings + IMigration bundling
            MigrationGenerator.Execute(ctx, compilation, this);
        }

        public string? GetDatabasePrefix()
        {
            var platform = Settings?.Database?.Platform;
            if (string.IsNullOrWhiteSpace(platform))
                return null;

            return platform.Trim().ToLowerInvariant() switch
            {
                "postgresql" or "postgre" or "postgres" => DatabasePrefixes.Postgresql,
                _ => null,
            };
        }
    }

    public static class DatabasePrefixes
    {
        public const string Postgresql = "Postgresql";
    }
}
