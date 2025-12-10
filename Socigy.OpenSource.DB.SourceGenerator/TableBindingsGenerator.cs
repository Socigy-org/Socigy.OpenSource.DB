using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.SourceGenerator.Templates;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Socigy.OpenSource.DB.SourceGenerator
{
    public static class TableBindingsGenerator
    {
        private static readonly string ColumnAttributeFullName = typeof(ColumnAttribute).FullName!;
        private static readonly string TableAttributeFullName = typeof(TableAttribute).FullName!;

        private static string GetNamespace(INamedTypeSymbol symbol)
        {
            var namespaces = new Stack<string>();
            var currentNamespace = symbol.ContainingNamespace;
            while (currentNamespace != null && !string.IsNullOrEmpty(currentNamespace.Name))
            {
                namespaces.Push(currentNamespace.Name);
                currentNamespace = currentNamespace.ContainingNamespace;
            }
            return string.Join(".", namespaces);
        }

        public static void Execute(SourceProductionContext ctx, Compilation compilation, ImmutableArray<ClassDeclarationSyntax> tables)
        {
            foreach (var table in tables)
            {
                var semanticModel = compilation.GetSemanticModel(table.SyntaxTree);
                if (semanticModel.GetDeclaredSymbol(table) is not INamedTypeSymbol tableSymbolInfo || tableSymbolInfo.IsStatic)
                    continue;

                var tableAttribute = tableSymbolInfo.GetAttributes().FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == TableAttributeFullName);
                if (tableAttribute == null ||
                    tableAttribute.ConstructorArguments.Length == 0 ||
                    tableAttribute.ConstructorArguments[0].Value == null)
                {
                    continue;
                }

                var tableColNameClassTemplate = new TableColumnNameClassTemplate()
                {
                    Namespace = GetNamespace(tableSymbolInfo),
                    ClassName = tableSymbolInfo.Name,
                    TableName = tableAttribute.ConstructorArguments.First().Value!.ToString(),
                    Columns = []
                };

                foreach (var member in table.Members)
                {
                    if (member is not PropertyDeclarationSyntax column)
                        continue;

                    semanticModel = compilation.GetSemanticModel(column.SyntaxTree);
                    if (semanticModel.GetDeclaredSymbol(column) is not IPropertySymbol symbolInfo || symbolInfo.IsStatic)
                        continue;

                    var columnInfo = new TableColumnNameClassTemplate.ColumnInfo()
                    {
                        Name = symbolInfo.Name,
                        DatabaseName = JsonNamingPolicy.SnakeCaseLower.ConvertName(symbolInfo.Name)
                    };

                    if (member.AttributeLists.Count > 0)
                    {
                        var columnAttribute = symbolInfo.GetAttributes().FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == ColumnAttributeFullName);
                        if (columnAttribute != null &&
                            columnAttribute.ConstructorArguments.Length > 0 &&
                            columnAttribute.ConstructorArguments[0].Value != null)
                        {
                            columnInfo.DatabaseName = columnAttribute.ConstructorArguments[0].Value!.ToString();
                        }
                    }

                    tableColNameClassTemplate.Columns.Add(columnInfo);
                }

                ctx.AddSource($"{tableColNameClassTemplate.ClassName}.g.cs", tableColNameClassTemplate.TransformText());
            }
        }
    }
}
