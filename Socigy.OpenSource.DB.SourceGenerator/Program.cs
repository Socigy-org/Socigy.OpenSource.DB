using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.SourceGenerator.Templates;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace Socigy.OpenSource.DB.SourceGenerator
{
    [Generator]
    public class Program : IIncrementalGenerator
    {
        public static readonly string _TableAttributeFullName = typeof(TableAttribute).FullName;
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            //Debugger.Launch();
            IncrementalValuesProvider<ClassDeclarationSyntax> tableClasses =
                context.SyntaxProvider
                        .ForAttributeWithMetadataName(
                            _TableAttributeFullName,
                            static (node, _) => node is ClassDeclarationSyntax,
                            static (ctx, _) =>
                            {
                                if (ctx.TargetNode is not ClassDeclarationSyntax classSyntax)
                                    return null;

                                if (ctx.SemanticModel.GetDeclaredSymbol(classSyntax) is not INamedTypeSymbol semantics)
                                    return null;

                                var tableAttribute = ctx.SemanticModel.Compilation.GetTypeByMetadataName(_TableAttributeFullName);
                                return semantics.GetAttributes().Any(x => SymbolEqualityComparer.Default.Equals(x.AttributeClass, tableAttribute))
                                    ? classSyntax
                                    : null;
                            })
                        .Where(x => x != null)!;

            context.RegisterSourceOutput(context.CompilationProvider.Combine(tableClasses.Collect()), Execute);
        }

        public static void Execute(SourceProductionContext ctx, (Compilation, ImmutableArray<ClassDeclarationSyntax>) tuple)
        {
            var (compilation, tables) = tuple;

            // [Table("_scg_migrations")]
            // MigrationManager bindings + IMigration bundling
            MigrationGenerator.Execute(ctx, compilation);

            // Table.Query() and other method generation
            TableBindingsGenerator.Execute(ctx, compilation, tables);

            // IServiceProvider and WebApplicationBuilder extensions
            ExtensionGenerator.Execute(ctx, compilation);
        }
    }
}
