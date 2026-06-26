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
using System.Text;
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
        public static readonly string FlagTableAttributeFullName = typeof(FlagTableAttribute).FullName;
        public static readonly string TableTypeAttributeFullName = typeof(TableTypeAttribute).FullName;
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

            IncrementalValuesProvider<ClassDeclarationSyntax> flagTableClasses =
                 context.SyntaxProvider
                         .ForAttributeWithMetadataName(
                             FlagTableAttributeFullName,
                             static (node, _) => node is ClassDeclarationSyntax,
                             static (ctx, _) =>
                             {
                                 if (ctx.TargetNode is not ClassDeclarationSyntax classSyntax)
                                     return null;

                                 if (ctx.SemanticModel.GetDeclaredSymbol(classSyntax) is not INamedTypeSymbol semantics)
                                     return null;

                                 var flagTableAttribute = ctx.SemanticModel.Compilation.GetTypeByMetadataName(FlagTableAttributeFullName);
                                 return semantics.GetAttributes().Any(x => SymbolEqualityComparer.Default.Equals(x.AttributeClass, flagTableAttribute))
                                    ? classSyntax
                                    : null;
                             })
                     .Where(x => x != null)!;

            IncrementalValuesProvider<ClassDeclarationSyntax> tableTypeClasses =
                 context.SyntaxProvider
                         .ForAttributeWithMetadataName(
                             TableTypeAttributeFullName,
                             static (node, _) => node is ClassDeclarationSyntax,
                             static (ctx, _) =>
                             {
                                 if (ctx.TargetNode is not ClassDeclarationSyntax classSyntax)
                                     return null;

                                 if (ctx.SemanticModel.GetDeclaredSymbol(classSyntax) is not INamedTypeSymbol semantics)
                                     return null;

                                 var tableTypeAttribute = ctx.SemanticModel.Compilation.GetTypeByMetadataName(TableTypeAttributeFullName);
                                 return semantics.GetAttributes().Any(x => SymbolEqualityComparer.Default.Equals(x.AttributeClass, tableTypeAttribute))
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

            var allTableClasses = tableClasses.Collect().Combine(flagTableClasses.Collect())
                .Combine(tableTypeClasses.Collect())
                .Select((pair, _) => pair.Left.Left.AddRange(pair.Left.Right).AddRange(pair.Right));

            context.RegisterSourceOutput(context.CompilationProvider.Combine(allTableClasses), Execute);

            var sqlFiles = context.AdditionalTextsProvider
                .Where(x => x.Path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));

            context.RegisterSourceOutput(
                context.CompilationProvider.Combine(sqlFiles.Collect()),
                (spc, pair) =>
                {
                    var (comp, texts) = pair;
                    if (comp.AssemblyName!.StartsWith("Socigy.OpenSource.DB"))
                        return;
                    ProcedureGenerator.Execute(spc, comp, texts);
                });
        }

        public void Execute(SourceProductionContext ctx, (Compilation, ImmutableArray<ClassDeclarationSyntax>) tuple)
        {
            var (compilation, tables) = tuple;

            if (compilation.AssemblyName!.StartsWith("Socigy.OpenSource.DB"))
                return; // Skip self-generation

            // No configured platform (no socigy.json, or no/unknown platform) -> emit nothing. Guards consumer
            // projects that run the analyzer transitively without a socigy.json from a hard generator failure
            // (a null DbEnginePrefix fed into a template throws ArgumentNullException('objectToConvert')).
            if (string.IsNullOrWhiteSpace(DatabasePrefix))
                return;

            // Table.Query() and other method generation
            TableBindingsGenerator.Execute(ctx, compilation, tables, this);

            // Testable context layer: I{Db}/{Db}Context, I{Table}Set/{Table}Set, {Db}Factory, Add{Db}Context
            ContextGenerator.Execute(ctx, compilation, tables, this);

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

            return platform!.Trim().ToLowerInvariant() switch
            {
                "postgresql" or "postgre" or "postgres" => DatabasePrefixes.Postgresql,
                _ => null,
            };
        }

        /// <summary>
        /// The base name used for generated C# identifiers (context interface, DI methods, factory, namespaces):
        /// <c>contextName</c> when set, else a valid identifier derived from <c>databaseName</c>. Distinct from
        /// the raw <c>databaseName</c>, which stays the connection-string / DI service / physical-database key.
        /// </summary>
        public string DatabaseTypeName
        {
            get
            {
                var ctx = Settings?.Database?.ContextName;
                return ToTypeIdentifier(!string.IsNullOrWhiteSpace(ctx) ? ctx! : (Settings?.Database?.DatabaseName ?? "UnnamedDb"));
            }
        }

        /// <summary>
        /// Derives a valid C# identifier from a (possibly lowercase, Postgres-conventional) name: keeps only
        /// letters/digits/underscore, prefixes '_' if it would start with a digit, and uppercases the first
        /// letter so an all-lowercase name like <c>identity</c> becomes <c>Identity</c> (avoiding CS8981).
        /// Mixed-case names like <c>MyDb</c> are unchanged.
        /// </summary>
        public static string ToTypeIdentifier(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "UnnamedDb";

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                if (char.IsLetterOrDigit(c) || c == '_')
                    sb.Append(c);

            if (sb.Length == 0) return "UnnamedDb";
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            if (char.IsLower(sb[0])) sb[0] = char.ToUpperInvariant(sb[0]);
            return sb.ToString();
        }
    }

    public static class DatabasePrefixes
    {
        public const string Postgresql = "Postgresql";
    }
}
