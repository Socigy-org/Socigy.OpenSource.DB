using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.SourceGenerator.Templates;
using Socigy.OpenSource.DB.SourceGenerator.Templates.CommandBuilders;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;

namespace Socigy.OpenSource.DB.SourceGenerator
{
    public static class TableBindingsGenerator
    {
        private static readonly string ColumnAttributeFullName = typeof(ColumnAttribute).FullName!;
        private static readonly string TableAttributeFullName = typeof(TableAttribute).FullName!;
        private static readonly string FlagTableAttributeFullName = typeof(FlagTableAttribute).FullName!;
        private static readonly string PrimaryKeyAttributeFullName = typeof(PrimaryKeyAttribute).FullName!;
        private static readonly string AutoIncrementAttributeFullName = typeof(AutoIncrementAttribute).FullName!;
        private static readonly string DefaultAttributeFullName = typeof(DefaultAttribute).FullName!;
        private static readonly string FlaggedEnumAttributeFullName = typeof(FlaggedEnumAttribute).FullName!;
        private static readonly string FlaggedEnumTableAttributeFullName = typeof(FlaggedEnumTableAttribute).FullName!;
        private static readonly string IgnoreAttributeFullName = typeof(IgnoreAttribute).FullName!;
        private static readonly string RawJsonColumnAttributeFullName = typeof(RawJsonColumnAttribute).FullName!;
        private static readonly string JsonColumnAttributeFullName = typeof(JsonColumnAttribute).FullName!;
        private static readonly string ValueConvertorAttributeFullName = typeof(ValueConvertorAttribute).FullName!;

        private static readonly DiagnosticDescriptor AutoIncrementTypeError = new(
            id: "SCGDB001",
            title: "[AutoIncrement] on unsupported type",
            messageFormat: "[AutoIncrement] can only be applied to short, int, or long — '{0}' is not supported",
            category: "Socigy.DB",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static string GetNamespace(INamedTypeSymbol symbol)
        {
            var namespaces = new System.Collections.Generic.Stack<string>();
            var currentNamespace = symbol.ContainingNamespace;
            while (currentNamespace != null && !string.IsNullOrEmpty(currentNamespace.Name))
            {
                namespaces.Push(currentNamespace.Name);
                currentNamespace = currentNamespace.ContainingNamespace;
            }
            return string.Join(".", namespaces);
        }

        public static void Execute(SourceProductionContext ctx, Compilation compilation, ImmutableArray<ClassDeclarationSyntax> tables, Program program)
        {
            foreach (var table in tables)
            {
                var semanticModel = compilation.GetSemanticModel(table.SyntaxTree);
                if (semanticModel.GetDeclaredSymbol(table) is not INamedTypeSymbol tableSymbolInfo || tableSymbolInfo.IsStatic)
                    continue;

                var allAttrs = tableSymbolInfo.GetAttributes();
                var tableAttribute = allAttrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == TableAttributeFullName);
                var flagTableAttribute = allAttrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == FlagTableAttributeFullName);

                string tableName;
                if (tableAttribute != null &&
                    tableAttribute.ConstructorArguments.Length > 0 &&
                    tableAttribute.ConstructorArguments[0].Value != null)
                {
                    tableName = tableAttribute.ConstructorArguments.First().Value!.ToString()!;
                }
                else if (flagTableAttribute != null &&
                    flagTableAttribute.ConstructorArguments.Length > 0 &&
                    flagTableAttribute.ConstructorArguments[0].Value != null)
                {
                    tableName = flagTableAttribute.ConstructorArguments.First().Value!.ToString()!;
                }
                else
                {
                    continue;
                }

                var tableColNameClassTemplate = new TableColumnNameClassTemplate()
                {
                    Namespace = GetNamespace(tableSymbolInfo),
                    ClassName = tableSymbolInfo.Name,
                    TableName = tableName,
                    Columns = []
                };

                var tableSyntaxTemplate = new TableSyntaxGeneratorTemplate()
                {
                    Namespace = tableColNameClassTemplate.Namespace,
                    ClassName = tableColNameClassTemplate.ClassName,
                    DbEnginePrefix = program.DatabasePrefix
                };

                var updateBuilderTemplate = new PostgresqlUpdateCommandBuilder()
                {
                    ClassName = tableColNameClassTemplate.ClassName,
                    Namespace = tableColNameClassTemplate.Namespace,
                    CustomPreClass = string.Empty,
                    CustomPostClass = string.Empty
                };
                ctx.AddSource($"{tableColNameClassTemplate.ClassName}.builder.update.g.cs", updateBuilderTemplate.TransformText());
                var deleteBuilderTemplate = new PostgresqlDeleteCommandBuilder()
                {
                    ClassName = tableColNameClassTemplate.ClassName,
                    Namespace = tableColNameClassTemplate.Namespace,
                    CustomPreClass = string.Empty,
                    CustomPostClass = string.Empty
                };
                ctx.AddSource($"{tableColNameClassTemplate.ClassName}.builder.delete.g.cs", deleteBuilderTemplate.TransformText());

                // Two-pass: first collect regular columns, then handle flagged enums
                var pendingFlaggedEnum = new List<(IPropertySymbol Symbol, AttributeData Attr, bool IsExplicit)>();

                foreach (var member in table.Members)
                {
                    if (member is not PropertyDeclarationSyntax column)
                        continue;

                    semanticModel = compilation.GetSemanticModel(column.SyntaxTree);
                    if (semanticModel.GetDeclaredSymbol(column) is not IPropertySymbol symbolInfo || symbolInfo.IsStatic)
                        continue;

                    // Skip [Ignore] properties
                    if (member.AttributeLists.Count > 0)
                    {
                        var ignoreAttr = symbolInfo.GetAttributes().FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == IgnoreAttributeFullName);
                        if (ignoreAttr != null) continue;
                    }

                    // Detect [FlaggedEnum] / [FlaggedEnumTable] — don't add to column list
                    if (member.AttributeLists.Count > 0)
                    {
                        var attrs = symbolInfo.GetAttributes();
                        var feAttr = attrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == FlaggedEnumAttributeFullName);
                        var fetAttr = attrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == FlaggedEnumTableAttributeFullName);
                        if (feAttr != null) { pendingFlaggedEnum.Add((symbolInfo, feAttr, false)); continue; }
                        if (fetAttr != null) { pendingFlaggedEnum.Add((symbolInfo, fetAttr, true)); continue; }
                    }

                    var columnInfo = new TableColumnNameClassTemplate.ColumnInfo()
                    {
                        Name = symbolInfo.Name,
                        Type = symbolInfo.Type.ToDisplayString(),
                        DatabaseName = JsonNamingPolicy.SnakeCaseLower.ConvertName(symbolInfo.Name)
                    };

                    if (member.AttributeLists.Count > 0)
                    {
                        var attrs = symbolInfo.GetAttributes();

                        var columnAttribute = attrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == ColumnAttributeFullName);
                        if (columnAttribute != null &&
                            columnAttribute.ConstructorArguments.Length > 0 &&
                            columnAttribute.ConstructorArguments[0].Value != null)
                            columnInfo.DatabaseName = columnAttribute.ConstructorArguments[0].Value!.ToString()!;

                        columnInfo.IsPrimaryKey = attrs.Any(x => x.AttributeClass?.ToDisplayString() == PrimaryKeyAttributeFullName);
                        columnInfo.HasDbDefault = attrs.Any(x => x.AttributeClass?.ToDisplayString() == DefaultAttributeFullName);

                        var autoIncrAttr = attrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == AutoIncrementAttributeFullName);
                        if (autoIncrAttr != null)
                        {
                            // Validate that the type is an integral type
                            var typeStr = symbolInfo.Type.ToDisplayString();
                            var isValidType = typeStr is "short" or "int" or "long"
                                or "short?" or "int?" or "long?"
                                or "System.Int16" or "System.Int32" or "System.Int64"
                                or "System.Int16?" or "System.Int32?" or "System.Int64?";
                            if (!isValidType)
                            {
                                ctx.ReportDiagnostic(Diagnostic.Create(
                                    AutoIncrementTypeError,
                                    symbolInfo.Locations.FirstOrDefault(),
                                    typeStr));
                            }

                            columnInfo.IsAutoIncrement = true;
                            var customSeqName = autoIncrAttr.ConstructorArguments.Length > 0
                                ? autoIncrAttr.ConstructorArguments[0].Value?.ToString()
                                : null;
                            columnInfo.SequenceName = !string.IsNullOrEmpty(customSeqName)
                                ? customSeqName
                                : $"{tableName}_{columnInfo.DatabaseName}_seq";
                        }

                        // [RawJsonColumn]
                        if (attrs.Any(x => x.AttributeClass?.ToDisplayString() == RawJsonColumnAttributeFullName))
                        {
                            columnInfo.IsJsonColumn = true;
                        }
                        // [JsonColumn(typeof(Ctx))]
                        var jsonColAttr = attrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == JsonColumnAttributeFullName);
                        if (jsonColAttr != null)
                        {
                            columnInfo.IsJsonColumn = true;
                            columnInfo.JsonContextType = jsonColAttr.ConstructorArguments.Length > 0
                                ? (jsonColAttr.ConstructorArguments[0].Value as INamedTypeSymbol)?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                  ?? jsonColAttr.ConstructorArguments[0].Value?.ToString()
                                : null;
                        }

                        // [ValueConvertor(typeof(TConvertor))] standalone attribute
                        var vcAttr = attrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == ValueConvertorAttributeFullName);
                        if (vcAttr != null && vcAttr.ConstructorArguments.Length > 0)
                        {
                            columnInfo.Converter = (vcAttr.ConstructorArguments[0].Value as INamedTypeSymbol)
                                ?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                ?? vcAttr.ConstructorArguments[0].Value?.ToString();
                        }
                        // Fallback: [Column(ValueConvertor = typeof(TConvertor))]
                        if (columnInfo.Converter == null && columnAttribute != null)
                        {
                            var vcNamedArg = columnAttribute.NamedArguments
                                .FirstOrDefault(na => na.Key == nameof(ColumnAttribute.ValueConvertor));
                            if (vcNamedArg.Key != null && vcNamedArg.Value.Value != null)
                            {
                                columnInfo.Converter = (vcNamedArg.Value.Value as INamedTypeSymbol)
                                    ?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                    ?? vcNamedArg.Value.Value?.ToString();
                            }
                        }
                    }

                    tableColNameClassTemplate.Columns.Add(columnInfo);
                    tableSyntaxTemplate.Columns.Add((
                        SourceName: symbolInfo.Name,
                        TypeName: symbolInfo.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        IsPrimaryKey: columnInfo.IsPrimaryKey,
                        Converter: columnInfo.Converter,
                        IsAutoIncrement: columnInfo.IsAutoIncrement,
                        SequenceName: columnInfo.SequenceName,
                        IsJsonColumn: columnInfo.IsJsonColumn,
                        JsonContextType: columnInfo.JsonContextType
                    ));
                }

                // Process flagged enum properties
                var mainPkColumns = tableColNameClassTemplate.Columns.Where(c => c.IsPrimaryKey).ToList();
                foreach (var (symInfo, attr, isExplicit) in pendingFlaggedEnum)
                {
                    if (isExplicit)
                    {
                        // [FlaggedEnumTable]: junction class is user-defined — just note it (no auto-generation)
                        // TODO: read junction class ForeignKey attrs to build PkMappings
                        continue;
                    }

                    // [FlaggedEnum] auto case
                    var enumTypeSymbol = symInfo.Type as INamedTypeSymbol;
                    if (enumTypeSymbol == null) continue;

                    var enumTableAttr = enumTypeSymbol.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == TableAttributeFullName);
                    if (enumTableAttr == null) continue;

                    var enumTableName = enumTableAttr.ConstructorArguments.FirstOrDefault().Value?.ToString() ?? "";

                    // Custom junction table name from [FlaggedEnum(TableName = "...")]
                    string? customJunctionTable = attr.NamedArguments
                        .FirstOrDefault(na => na.Key == nameof(FlaggedEnumAttribute.TableName))
                        .Value.Value?.ToString();
                    var junctionTableName = customJunctionTable ?? $"{tableName}_{enumTableName}";

                    // Parse key mappings (alternating propName, junctionColName)
                    var keyMappingsList = attr.ConstructorArguments.Length > 0
                        ? (attr.ConstructorArguments[0].Values.Select(v => v.Value?.ToString()).ToList())
                        : new List<string?>();

                    // Build PK mappings
                    var pkMappings = new List<(string PropName, string MainPkCol, string JunctionFkCol)>();
                    foreach (var pk in mainPkColumns)
                    {
                        string? junctionFkCol = null;
                        for (int k = 0; k + 1 < keyMappingsList.Count; k += 2)
                        {
                            if (keyMappingsList[k] == pk.Name)
                            {
                                junctionFkCol = keyMappingsList[k + 1];
                                break;
                            }
                        }
                        junctionFkCol ??= $"{tableName}_{pk.DatabaseName}";
                        pkMappings.Add((pk.Name, pk.DatabaseName, junctionFkCol));
                    }

                    // Enum FK column
                    string enumFkCol = $"{enumTableName}_id";
                    for (int k = 0; k + 1 < keyMappingsList.Count; k += 2)
                    {
                        if (keyMappingsList[k] == enumTypeSymbol.Name)
                        {
                            enumFkCol = keyMappingsList[k + 1]!;
                            break;
                        }
                    }

                    tableSyntaxTemplate.FlaggedEnumProperties.Add(new TableSyntaxGeneratorTemplate.FlaggedEnumPropertyInfo
                    {
                        SourceName = symInfo.Name,
                        EnumTypeFullName = symInfo.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        JunctionTable = junctionTableName,
                        MainTable = tableName,
                        PkMappings = pkMappings,
                        EnumFkColumn = enumFkCol
                    });
                }

                ctx.AddSource($"{tableColNameClassTemplate.ClassName}.table.g.cs", tableColNameClassTemplate.TransformText());
                ctx.AddSource($"{tableColNameClassTemplate.ClassName}SyntaxMethods.table.g.cs", tableSyntaxTemplate.TransformText());
            }
        }
    }
}
