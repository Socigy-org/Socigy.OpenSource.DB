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
        private static readonly string IndexAttributeFullName = typeof(IndexAttribute).FullName!;
        private static readonly string TableAttributeFullName = typeof(TableAttribute).FullName!;
        private static readonly string FlagTableAttributeFullName = typeof(FlagTableAttribute).FullName!;
        private static readonly string TableTypeAttributeFullName = typeof(TableTypeAttribute).FullName!;
        private static readonly string PrimaryKeyAttributeFullName = typeof(PrimaryKeyAttribute).FullName!;
        private static readonly string AutoIncrementAttributeFullName = typeof(AutoIncrementAttribute).FullName!;
        private static readonly string DefaultAttributeFullName = typeof(DefaultAttribute).FullName!;
        private static readonly string FlaggedEnumAttributeFullName = typeof(FlaggedEnumAttribute).FullName!;
        private static readonly string FlaggedEnumTableAttributeFullName = typeof(FlaggedEnumTableAttribute).FullName!;
        private static readonly string IgnoreAttributeFullName = typeof(IgnoreAttribute).FullName!;
        private static readonly string RawJsonColumnAttributeFullName = typeof(RawJsonColumnAttribute).FullName!;
        private static readonly string JsonColumnAttributeFullName = typeof(JsonColumnAttribute).FullName!;
        private static readonly string ValueConvertorAttributeFullName = typeof(ValueConvertorAttribute).FullName!;
        private static readonly string EncryptedAttributeFullName = typeof(EncryptedAttribute).FullName!;
        private static readonly string StringLengthAttributeFullName = typeof(StringLengthAttribute).FullName!;

        // Instance properties that are candidate columns, walked across the BASE CHAIN (most-derived first,
        // deduped by name so a shadowing/overriding derived property wins). Static properties and indexers are
        // excluded here (matching the previous syntax-based filter); further filtering (writable / [Ignore] /
        // [FlaggedEnum]) happens in the caller. GetMembers() already aggregates all `partial` declarations of a
        // type, so this also picks up properties declared in a different partial than the one carrying [Table].
        private static IEnumerable<IPropertySymbol> EnumerateColumnProperties(INamedTypeSymbol type)
        {
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            for (INamedTypeSymbol? t = type; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
                foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
                    if (!p.IsStatic && !p.IsIndexer && seen.Add(p.Name))
                        yield return p;
        }

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
            // Emit [SetsRequiredMembers] on generated ctors only when the consumer's compilation has the
            // attribute (net7+/C# 11). Lets `required` members satisfy the new() constraint without breaking
            // older (e.g. netstandard2.0) consumers, which can't use `required` anyway.
            string setsRequiredAttr =
                compilation.GetTypeByMetadataName("System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute") != null
                    ? "[global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]"
                    : "";

            // A class may match more than one collected provider (e.g. [Table] + [TableType]); process once.
            var processed = new HashSet<string>();
            foreach (var table in tables)
            {
                var semanticModel = compilation.GetSemanticModel(table.SyntaxTree);
                if (semanticModel.GetDeclaredSymbol(table) is not INamedTypeSymbol tableSymbolInfo || tableSymbolInfo.IsStatic)
                    continue;

                if (!processed.Add(tableSymbolInfo.ToDisplayString()))
                    continue;

                // The generated partial declares a non-generic, top-level `partial class <Name>`. A generic
                // (User<T>) or nested (Outer.Inner) [Table] would produce an uncompilable partial (CS0264/CS0260)
                // with no explanation — fail with a clear diagnostic and skip codegen for it instead.
                if (tableSymbolInfo.IsGenericType || tableSymbolInfo.ContainingType != null)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnsupportedTableShape,
                        tableSymbolInfo.Locations.FirstOrDefault(), tableSymbolInfo.Name,
                        tableSymbolInfo.IsGenericType ? "generic" : "a nested type"));
                    continue;
                }

                var allAttrs = tableSymbolInfo.GetAttributes();
                var tableAttribute = allAttrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == TableAttributeFullName);
                var flagTableAttribute = allAttrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == FlagTableAttributeFullName);
                var tableTypeAttribute = allAttrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == TableTypeAttributeFullName);
                bool isTableType = tableTypeAttribute != null;

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
                else if (isTableType)
                {
                    // A pure [TableType] has no fixed name; use a snake_case default for the const TableName
                    // (the runtime name always wins through DynamicTable<T>).
                    tableName = ToSnakeCase(tableSymbolInfo.Name);
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

                // Hint names must be unique across the whole compilation. Two [Table] classes with the same simple
                // name in different namespaces (e.g. Auth.User and Billing.User) would otherwise produce identical
                // hint names and crash the generator with an opaque "hintName already added". Qualify with the
                // namespace so each gets a distinct, stable file id.
                string hintBase = (string.IsNullOrEmpty(tableColNameClassTemplate.Namespace)
                    ? tableColNameClassTemplate.ClassName
                    : tableColNameClassTemplate.Namespace + "." + tableColNameClassTemplate.ClassName).Replace('.', '_');

                var tableSyntaxTemplate = new TableSyntaxGeneratorTemplate()
                {
                    Namespace = tableColNameClassTemplate.Namespace,
                    ClassName = tableColNameClassTemplate.ClassName,
                    DbEnginePrefix = program.DatabasePrefix,
                    SetsRequiredMembersAttribute = setsRequiredAttr
                };

                var updateBuilderTemplate = new PostgresqlUpdateCommandBuilder()
                {
                    ClassName = tableColNameClassTemplate.ClassName,
                    Namespace = tableColNameClassTemplate.Namespace,
                    CustomPreClass = string.Empty,
                    CustomPostClass = string.Empty
                };
                ctx.AddSource($"{hintBase}.builder.update.g.cs", updateBuilderTemplate.TransformText());
                var deleteBuilderTemplate = new PostgresqlDeleteCommandBuilder()
                {
                    ClassName = tableColNameClassTemplate.ClassName,
                    Namespace = tableColNameClassTemplate.Namespace,
                    CustomPreClass = string.Empty,
                    CustomPostClass = string.Empty
                };
                ctx.AddSource($"{hintBase}.builder.delete.g.cs", deleteBuilderTemplate.TransformText());

                // Two-pass: first collect regular columns, then handle flagged enums
                var pendingFlaggedEnum = new List<(IPropertySymbol Symbol, AttributeData Attr, bool IsExplicit)>();

                // Walk the symbol's members across its BASE CHAIN (most-derived first, dedup by name). A [Table]
                // that inherits columns from a base class, or splits its properties across several `partial`
                // declarations, previously dropped every inherited / other-partial property because this loop read
                // only the one ClassDeclarationSyntax's own members — silent data loss, and inconsistent with the
                // procedure placeholder resolver / DTO mapper which already walk the base. For a flat single-class
                // [Table] (the common case) GetMembers() yields the same properties in the same order, so generated
                // output is unchanged; the walk only ADDS inherited / split-partial columns.
                foreach (var symbolInfo in EnumerateColumnProperties(tableSymbolInfo))
                {
                    // A mapped column must be writable: the generated materialization assigns to it
                    // (row.Prop = value). A get-only / expression-bodied (computed) / init-only property can't be
                    // set that way, so treat it as a non-column (like [Ignore]) instead of emitting code that
                    // fails to compile (CS0200 / CS8852).
                    if (symbolInfo.SetMethod == null || symbolInfo.SetMethod.IsInitOnly)
                        continue;

                    var memberAttrs = symbolInfo.GetAttributes();

                    var ignoreAttr = memberAttrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == IgnoreAttributeFullName);
                    if (ignoreAttr != null) continue;

                    // Detect [FlaggedEnum] / [FlaggedEnumTable] — don't add to column list
                    {
                        var feAttr = memberAttrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == FlaggedEnumAttributeFullName);
                        var fetAttr = memberAttrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == FlaggedEnumTableAttributeFullName);
                        if (feAttr != null) { pendingFlaggedEnum.Add((symbolInfo, feAttr, false)); continue; }
                        if (fetAttr != null) { pendingFlaggedEnum.Add((symbolInfo, fetAttr, true)); continue; }
                    }

                    var columnInfo = new TableColumnNameClassTemplate.ColumnInfo()
                    {
                        Name = symbolInfo.Name,
                        Type = symbolInfo.Type.ToDisplayString(),
                        DatabaseName = ColumnNaming.ResolveDbColumnName(symbolInfo, ColumnAttributeFullName)
                    };

                    // Record the underlying integral type for an enum (unwrapping Nullable<TEnum>) so a baked
                    // CREATE TABLE (DynamicTable.InstantiateAsync) emits the integer type the insert path binds,
                    // not "text" (which the insert would then fail to write an integer into).
                    {
                        ITypeSymbol __t = symbolInfo.Type;
                        if (__t is INamedTypeSymbol __nt && __nt.OriginalDefinition?.SpecialType == SpecialType.System_Nullable_T
                            && __nt.TypeArguments.Length == 1)
                            __t = __nt.TypeArguments[0];
                        if (__t.TypeKind == TypeKind.Enum && __t is INamedTypeSymbol __enum && __enum.EnumUnderlyingType != null)
                            columnInfo.EnumUnderlyingType = __enum.EnumUnderlyingType.ToDisplayString();
                    }

                    if (memberAttrs.Length > 0)
                    {
                        var attrs = memberAttrs;

                        var columnAttribute = attrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == ColumnAttributeFullName);
                        // SCGDB018 — an explicit but empty [Column("")] name; ColumnNaming already fell back to snake_case.
                        if (columnAttribute != null &&
                            columnAttribute.ConstructorArguments.Length > 0 &&
                            columnAttribute.ConstructorArguments[0].Value is string colNameArg &&
                            string.IsNullOrWhiteSpace(colNameArg))
                            ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.EmptyColumnName, symbolInfo.Locations.FirstOrDefault(), symbolInfo.Name));

                        columnInfo.IsPrimaryKey = attrs.Any(x => x.AttributeClass?.ToDisplayString() == PrimaryKeyAttributeFullName);
                        var defaultAttr = attrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == DefaultAttributeFullName);
                        columnInfo.HasDbDefault = defaultAttr != null;
                        // Capture the [Default("expr")] value so the baked CREATE TABLE can emit a DEFAULT clause
                        // (a naked [Default] has no ctor arg -> null -> no clause, matching the migration generator).
                        if (defaultAttr != null && defaultAttr.ConstructorArguments.Length > 0)
                            columnInfo.DefaultValueSql = defaultAttr.ConstructorArguments[0].Value?.ToString();

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
                                    Diagnostics.AutoIncrementTypeError,
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

                        // [Encrypted] — stored as bytea, encrypted on write / decrypted on read.
                        var encAttr = attrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == EncryptedAttributeFullName);
                        columnInfo.IsEncrypted = encAttr != null;
                        if (encAttr != null)
                        {
                            var autoDecryptArg = encAttr.NamedArguments
                                .FirstOrDefault(na => na.Key == nameof(EncryptedAttribute.AutoDecrypt));
                            if (autoDecryptArg.Key != null && autoDecryptArg.Value.Value is bool ad)
                                columnInfo.EncryptAutoDecrypt = ad;

                            var profileArg = encAttr.NamedArguments
                                .FirstOrDefault(na => na.Key == nameof(EncryptedAttribute.Profile));
                            if (profileArg.Key != null && profileArg.Value.Value is string prof && !string.IsNullOrEmpty(prof))
                                columnInfo.EncryptionProfile = prof;
                        }
                        // [Encrypted] stores non-deterministic bytea ciphertext, so it cannot also be a JSON column,
                        // carry a value convertor, or be length-constrained: [StringLength] would (in the migration
                        // analyzer) produce an order-dependent character varying(n) DDL that contradicts the bytea the
                        // runtime writes. Fail the build loudly rather than emit a silently-wrong/ambiguous column.
                        bool hasStringLength = attrs.Any(x => x.AttributeClass?.ToDisplayString() == StringLengthAttributeFullName);
                        if (columnInfo.IsEncrypted && (columnInfo.IsJsonColumn || !string.IsNullOrEmpty(columnInfo.Converter) || hasStringLength))
                            ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.EncryptedComboError, symbolInfo.Locations.FirstOrDefault(), symbolInfo.Name));
                        // SCGDB023 — an encrypted column is non-deterministic bytea; it cannot be a key or auto-increment.
                        if (columnInfo.IsEncrypted && (columnInfo.IsPrimaryKey || columnInfo.IsAutoIncrement))
                            ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.EncryptedKeyColumn, symbolInfo.Locations.FirstOrDefault(), symbolInfo.Name));
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
                        JsonContextType: columnInfo.JsonContextType,
                        IsEncrypted: columnInfo.IsEncrypted,
                        EncryptAutoDecrypt: columnInfo.EncryptAutoDecrypt,
                        EncryptionProfile: columnInfo.EncryptionProfile
                    ));
                }

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

                // SCGDB017 / SCGDB016 — table definition quality checks.
                if (tableColNameClassTemplate.Columns.Count == 0)
                    ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.TableNoColumns, tableSymbolInfo.Locations.FirstOrDefault(), tableSymbolInfo.Name));
                // SCGDB016 is about the generated update/delete-by-PK operations. A pure [TableType] (no [Table])
                // is a runtime-named row shape used for projections and need not have a primary key, so don't warn.
                else if (!tableColNameClassTemplate.Columns.Any(c => c.IsPrimaryKey) && !(isTableType && tableAttribute == null))
                    ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.TableNoPrimaryKey, tableSymbolInfo.Locations.FirstOrDefault(), tableSymbolInfo.Name));

                // SCGDB024 — two properties resolving to the same DB column name would emit colliding SQL.
                var seenColumnNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var col in tableColNameClassTemplate.Columns)
                    if (!seenColumnNames.Add(col.DatabaseName))
                        ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.DuplicateColumnName, tableSymbolInfo.Locations.FirstOrDefault(), tableSymbolInfo.Name, col.DatabaseName));

                // SCGDB026 — an [Index] column reference that matches no mapped property. nameof() is checked by
                // the compiler, but a string literal is not, so a typo would otherwise only surface when the
                // generated migration failed to apply against the database.
                ReportUnknownIndexProperties(ctx, tableSymbolInfo, tableColNameClassTemplate.Columns);

                ctx.AddSource($"{hintBase}.table.g.cs", tableColNameClassTemplate.TransformText());
                ctx.AddSource($"{hintBase}SyntaxMethods.table.g.cs", tableSyntaxTemplate.TransformText());

                if (isTableType)
                    ctx.AddSource(
                        $"{hintBase}.tabletype.g.cs",
                        EmitTableTypePartial(tableColNameClassTemplate.Namespace, tableColNameClassTemplate.ClassName, tableColNameClassTemplate.Columns));
            }

            // Once per assembly that declares at least one [Table]/[TableType]: install the Npgsql binary-COPY
            // bridge into the provider-agnostic Core, enabling BulkCopy / DynamicTable.InsertMultipleCopyAsync.
            if (processed.Count > 0)
                ctx.AddSource("__SocigyBulkCopyBridge.g.cs", BulkCopyBridgeSource);
        }

        /// <summary>
        /// Reports every <c>[Index]</c> column reference on <paramref name="tableSymbol"/> that does not name a
        /// mapped property, whether it appears in the class-level column list or in one of the option arrays.
        /// </summary>
        private static void ReportUnknownIndexProperties(
            SourceProductionContext ctx,
            INamedTypeSymbol tableSymbol,
            List<TableColumnNameClassTemplate.ColumnInfo> columns)
        {
            var known = new HashSet<string>(columns.Select(c => c.Name), StringComparer.Ordinal);

            void Check(AttributeData attribute, IEnumerable<string> references)
            {
                foreach (var reference in references)
                {
                    if (string.IsNullOrWhiteSpace(reference) || known.Contains(reference)) continue;

                    var location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                                   ?? tableSymbol.Locations.FirstOrDefault();
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.IndexUnknownProperty, location, tableSymbol.Name, reference));
                }
            }

            // Class-level [Index(...)]: the constructor params are the key columns.
            foreach (var attribute in tableSymbol.GetAttributes()
                         .Where(a => a.AttributeClass?.ToDisplayString() == IndexAttributeFullName))
            {
                Check(attribute, ReadStringArrayArgument(attribute.ConstructorArguments.FirstOrDefault()));
                Check(attribute, ReadIndexOptionColumns(attribute));
            }

            // Property-level [Index]: the key column is the property itself, so only the options can be wrong.
            foreach (var member in tableSymbol.GetMembers().OfType<IPropertySymbol>())
                foreach (var attribute in member.GetAttributes()
                             .Where(a => a.AttributeClass?.ToDisplayString() == IndexAttributeFullName))
                    Check(attribute, ReadIndexOptionColumns(attribute));
        }

        /// <summary>Column references from the named arguments that take property names.</summary>
        private static IEnumerable<string> ReadIndexOptionColumns(AttributeData attribute)
        {
            foreach (var named in attribute.NamedArguments)
            {
                if (named.Key != nameof(IndexAttribute.Include) &&
                    named.Key != nameof(IndexAttribute.DescendingColumns) &&
                    named.Key != nameof(IndexAttribute.NullsFirstColumns) &&
                    named.Key != nameof(IndexAttribute.NullsLastColumns))
                    continue;

                foreach (var value in ReadStringArrayArgument(named.Value))
                    yield return value;
            }
        }

        private static IEnumerable<string> ReadStringArrayArgument(TypedConstant constant)
        {
            if (constant.Kind != TypedConstantKind.Array) return Array.Empty<string>();
            return constant.Values.Select(v => v.Value as string).Where(v => v != null);
        }

        // Registers the Npgsql binary-COPY implementation into Core's BulkCopySupport at module load. Kept as
        // a hand-written source string (not a T4 template) so it compiles into the consumer's assembly, where
        // Npgsql is available, without depending on the build regenerating the preprocessed templates.
        private const string BulkCopyBridgeSource = @"#pragma warning disable
#nullable enable
namespace Socigy.OpenSource.DB.Generated
{
    internal static class __SocigyBulkCopyBridge
    {
        [global::System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Initialize()
            => global::Socigy.OpenSource.DB.Core.Bulk.BulkCopySupport.Register(CopyAsync);

        private static async global::System.Threading.Tasks.Task<ulong> CopyAsync(
            global::System.Data.Common.DbConnection connection,
            global::System.Data.Common.DbTransaction? transaction,
            string copyCommand,
            global::Socigy.OpenSource.DB.Core.Bulk.CopyColumn[] columns,
            global::System.Collections.Generic.IReadOnlyList<object> rows,
            global::System.Threading.CancellationToken cancellationToken)
        {
            var npg = connection as global::Npgsql.NpgsqlConnection
                ?? throw new global::System.InvalidOperationException(""Binary COPY requires an NpgsqlConnection."");

            await using var importer = await npg.BeginBinaryImportAsync(copyCommand, cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < rows.Count; i++)
            {
                object row = rows[i];
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                for (int c = 0; c < columns.Length; c++)
                {
                    var col = columns[c];
                    object? value = col.GetValue(row);
                    if (value is null || value is global::System.DBNull)
                    {
                        await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (col.IsEncrypted)
                    {
                        await importer.WriteAsync(value, global::NpgsqlTypes.NpgsqlDbType.Bytea, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (col.IsJson)
                    {
                        await importer.WriteAsync(value, global::NpgsqlTypes.NpgsqlDbType.Jsonb, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    // Coerce only when the value is *actually* an enum at runtime (a value convertor may have
                    // produced a non-enum DB representation for an enum-declared column). Matches insert/update.
                    if (value.GetType().IsEnum)
                        value = global::System.Convert.ChangeType(value, global::System.Enum.GetUnderlyingType(value.GetType()));
                    else if (value is global::System.DateTime __dt && __dt.Kind == global::System.DateTimeKind.Utc)
                        // Binary COPY is strict: a 'timestamp without time zone' column rejects a Kind=Utc
                        // DateTime. Relabel it Unspecified (same wall-clock) so DateTime.UtcNow round-trips
                        // instead of throwing. The parameterized path never hits this because it does not set
                        // an explicit NpgsqlDbType for a non-null DateTime, letting Npgsql infer from Kind.
                        value = global::System.DateTime.SpecifyKind(__dt, global::System.DateTimeKind.Unspecified);
                    // 'timestamptz' only accepts a DateTimeOffset at offset 0; normalize to the same UTC instant.
                    else if (value is global::System.DateTimeOffset __dto && __dto.Offset != global::System.TimeSpan.Zero)
                        value = __dto.ToUniversalTime();
                    // No wire mapping for unsigned CLR types; widen to what GetDbType targets (Integer/Bigint/Numeric).
                    else if (value is ushort __us) value = (int)__us;
                    else if (value is uint __ui) value = (long)__ui;
                    else if (value is ulong __ul) value = (decimal)__ul;
                    // Derive the wire type from the (normalized) runtime value, not the declared column type: a
                    // value convertor may store an enum-declared column as a different type (e.g. text), and the
                    // value is non-null here (nulls were written above). For non-convertor columns the normalized
                    // value's type maps to the same NpgsqlDbType the declared type would.
                    await importer.WriteAsync(value, GetDbType(value.GetType()), cancellationToken).ConfigureAwait(false);
                }
            }
            return await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        // Mirrors PostgresqlInsertCommandBuilder<T>.GetDbType so the COPY wire types match the parameterized path.
        private static global::NpgsqlTypes.NpgsqlDbType GetDbType(global::System.Type type)
        {
            type = global::System.Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsEnum)
                type = global::System.Enum.GetUnderlyingType(type);

            if (type == typeof(short) || type == typeof(byte) || type == typeof(sbyte)) return global::NpgsqlTypes.NpgsqlDbType.Smallint;
            if (type == typeof(int) || type == typeof(ushort)) return global::NpgsqlTypes.NpgsqlDbType.Integer;
            if (type == typeof(long) || type == typeof(uint)) return global::NpgsqlTypes.NpgsqlDbType.Bigint;
            if (type == typeof(ulong)) return global::NpgsqlTypes.NpgsqlDbType.Numeric;
            if (type == typeof(string)) return global::NpgsqlTypes.NpgsqlDbType.Text;
            if (type == typeof(bool)) return global::NpgsqlTypes.NpgsqlDbType.Boolean;
            if (type == typeof(global::System.DateTime)) return global::NpgsqlTypes.NpgsqlDbType.Timestamp;
            if (type == typeof(global::System.DateTimeOffset)) return global::NpgsqlTypes.NpgsqlDbType.TimestampTz;
            if (type == typeof(global::System.TimeSpan)) return global::NpgsqlTypes.NpgsqlDbType.Interval;
#if NET6_0_OR_GREATER
            if (type == typeof(global::System.DateOnly)) return global::NpgsqlTypes.NpgsqlDbType.Date;
            if (type == typeof(global::System.TimeOnly)) return global::NpgsqlTypes.NpgsqlDbType.Time;
#endif
            if (type == typeof(float)) return global::NpgsqlTypes.NpgsqlDbType.Real;
            if (type == typeof(double)) return global::NpgsqlTypes.NpgsqlDbType.Double;
            if (type == typeof(decimal)) return global::NpgsqlTypes.NpgsqlDbType.Numeric;
            if (type == typeof(global::System.Guid)) return global::NpgsqlTypes.NpgsqlDbType.Uuid;
            if (type == typeof(byte[])) return global::NpgsqlTypes.NpgsqlDbType.Bytea;
            if (type == typeof(char)) return global::NpgsqlTypes.NpgsqlDbType.Char;
            return global::NpgsqlTypes.NpgsqlDbType.Text;
        }
    }
}
#nullable restore
";

        // Emits the [TableType] partial: implements IDbTableType<T> (delegating to the generated statics),
        // the WithTableName / MapTypeAsync factories, and a baked CREATE TABLE for the declared shape.
        private static string EmitTableTypePartial(string ns, string className, List<TableColumnNameClassTemplate.ColumnInfo> columns)
        {
            string columnDefs = BuildCreateTableColumnDefs(columns);
            string sequencePrefix = BuildCreateSequencesPrefix(columns);
            return $@"#pragma warning disable
#nullable enable
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Socigy.OpenSource.DB.Core.CommandBuilders;
using Socigy.OpenSource.DB.Core.Dynamic;
using Socigy.OpenSource.DB.Core.Interfaces;

namespace {ns}
{{
    public partial class {className} : global::Socigy.OpenSource.DB.Core.Interfaces.IDbTableType<{className}>
    {{
        int[] global::Socigy.OpenSource.DB.Core.Interfaces.IDbTableType<{className}>.ResolveOrdinals(DbDataReader reader, System.Collections.Generic.Dictionary<string, string>? columnOverrides)
            => GetColumnOrdinals(reader, columnOverrides);

        {className} global::Socigy.OpenSource.DB.Core.Interfaces.IDbTableType<{className}>.MaterializeRow(DbDataReader reader, int[] ordinals)
            => ConvertFrom(reader, ordinals);

        global::Socigy.OpenSource.DB.Core.CommandBuilders.InsertColumnDescriptor[] global::Socigy.OpenSource.DB.Core.Interfaces.IDbTableType<{className}>.InsertColumns(bool includeAutoIncrement)
            => GetInsertPlan(includeAutoIncrement).Columns;

        string global::Socigy.OpenSource.DB.Core.Interfaces.IDbTableType<{className}>.GetCreateTableSql(string tableName, bool ifNotExists)
            => ""{sequencePrefix}CREATE TABLE "" + (ifNotExists ? ""IF NOT EXISTS "" : """") + ""\"""" + tableName.Replace(""\"""", ""\""\"""") + ""\"" ({columnDefs})"";

        /// <summary>Binds this table type to a runtime table name.</summary>
        public static global::Socigy.OpenSource.DB.Core.Dynamic.DynamicTable<{className}> WithTableName(string tableName)
            => new global::Socigy.OpenSource.DB.Core.Dynamic.DynamicTable<{className}>(tableName);

        /// <summary>Binds to a runtime table name and auto-discovers its extra (undeclared) columns (cached).</summary>
        public static global::System.Threading.Tasks.Task<global::Socigy.OpenSource.DB.Core.Dynamic.DynamicTable<{className}>> MapTypeAsync(string tableName, DbConnection connection, bool force = false, CancellationToken cancellationToken = default)
            => WithTableName(tableName).WithConnection(connection).MapTypeAsync(force, cancellationToken);
    }}
}}
#nullable disable
";
        }

        // Builds the baked column-definition list for a runtime CREATE TABLE (escaped for a C# string literal).
        private static string BuildCreateTableColumnDefs(List<TableColumnNameClassTemplate.ColumnInfo> columns)
        {
            var parts = new List<string>();
            foreach (var col in columns)
            {
                string baseType = col.Type.TrimEnd('?').Trim();
                string pgType;
                bool serial = false;
                if (col.IsAutoIncrement)
                {
                    if (!string.IsNullOrEmpty(col.SequenceName))
                    {
                        // A custom [AutoIncrement("name")] sequence: 'serial' would create {table}_{col}_seq, so the
                        // runtime sequence accessors (which use the custom name) would hit a nonexistent sequence.
                        // Emit the integer type with an explicit nextval of the custom sequence (the CREATE SEQUENCE
                        // for it is prepended by BuildCreateSequencesPrefix).
                        string intType = baseType.ToLowerInvariant() switch
                        {
                            "short" or "int16" or "system.int16" => "smallint",
                            "long" or "int64" or "system.int64" => "bigint",
                            _ => "integer",
                        };
                        pgType = intType + " DEFAULT nextval('\\\"" + col.SequenceName + "\\\"')";
                    }
                    else
                    {
                        pgType = baseType.ToLowerInvariant() switch
                        {
                            "short" or "int16" or "system.int16" => "smallserial",
                            "long" or "int64" or "system.int64" => "bigserial",
                            _ => "serial",
                        };
                    }
                    serial = true;
                }
                else if (col.IsEncrypted)
                    pgType = "bytea";
                else if (col.IsJsonColumn)
                    pgType = "jsonb";
                else if (!string.IsNullOrEmpty(col.EnumUnderlyingType))
                    // An enum is stored as its underlying integer (the insert/COPY paths bind it that way), so the
                    // baked DDL must use the integral type, not "text".
                    pgType = MapPgType(col.EnumUnderlyingType);
                else
                    pgType = MapPgType(baseType);

                // Nullability follows the declared type exactly (col.Type carries the NRT "?" for a nullable
                // reference type, e.g. "string?"), matching the migration analyzer, which marks a non-nullable
                // reference type NOT NULL. Forcing every reference type nullable made the baked CREATE TABLE create
                // a non-nullable string/byte[] column as NULLABLE while the migration created it NOT NULL — the two
                // ways of creating the same table diverged, and a NULL could land in a non-nullable CLR property.
                bool nullable = col.Type.EndsWith("?");
                bool notNull = !serial && (col.IsPrimaryKey || !nullable);

                // Quotes are doubled so the result is a valid C# string literal segment.
                var def = "\\\"" + col.DatabaseName + "\\\" " + pgType;
                if (notNull) def += " NOT NULL";
                // A [Default("expr")] column emits a DEFAULT clause so the baked CREATE TABLE matches the migration
                // generator. Without it a non-nullable [Default] column was created NOT NULL with no default, so an
                // insert that omits it (the ServerDefaults / ExcludeAutoFields path) failed. Serial/auto-increment
                // columns already carry their DEFAULT nextval in pgType; a naked [Default] has no value to emit.
                if (!serial && !string.IsNullOrEmpty(col.DefaultValueSql))
                    def += " DEFAULT " + TranslateDefaultSql(col.DefaultValueSql);
                parts.Add(def);
            }

            var pkCols = columns.Where(c => c.IsPrimaryKey).ToList();
            if (pkCols.Count > 0)
                parts.Add("PRIMARY KEY (" + string.Join(", ", pkCols.Select(c => "\\\"" + c.DatabaseName + "\\\"")) + ")");

            return string.Join(", ", parts);
        }

        // Translates a DbDefaults token to its SQL default expression for the baked CREATE TABLE — the same mapping
        // the migration generator (PostgreSqlGenerator.TranslateDefault) uses, so a [TableType] instantiated at
        // runtime and the same model migrated produce identical DEFAULTs. A non-token value is emitted verbatim.
        // The result is escaped for the surrounding C# string literal the DDL is assembled into.
        private static string TranslateDefaultSql(string token)
        {
            string sql = token switch
            {
                DbDefaults.Guid.Random => "gen_random_uuid()",
                DbDefaults.Guid.Sequential => "uuid_generate_v1mc()",
                DbDefaults.Time.Now => "timezone('utc', now())",
                DbDefaults.Time.NowLocal => "now()",
                DbDefaults.Time.Date => "current_date",
                DbDefaults.Bool.True => "TRUE",
                DbDefaults.Bool.False => "FALSE",
                DbDefaults.Number.Zero => "0",
                DbDefaults.Number.One => "1",
                DbDefaults.Text.Empty => "''",
                _ => token
            };
            return sql.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // Emits "CREATE SEQUENCE IF NOT EXISTS \"name\" AS <type>; " (escaped for the C# string literal) for each
        // [AutoIncrement("name")] column with a CUSTOM sequence name, so the baked CREATE TABLE's nextval default
        // and the runtime sequence accessors target a sequence that actually exists. Empty for the default
        // (serial) case, which auto-creates {table}_{col}_seq.
        private static string BuildCreateSequencesPrefix(List<TableColumnNameClassTemplate.ColumnInfo> columns)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var col in columns)
            {
                if (!col.IsAutoIncrement || string.IsNullOrEmpty(col.SequenceName)) continue;
                string baseType = col.Type.TrimEnd('?').Trim().ToLowerInvariant();
                string seqType = baseType switch
                {
                    "short" or "int16" or "system.int16" => "smallint",
                    "long" or "int64" or "system.int64" => "bigint",
                    _ => "integer",
                };
                sb.Append("CREATE SEQUENCE IF NOT EXISTS \\\"").Append(col.SequenceName).Append("\\\" AS ").Append(seqType).Append("; ");
            }
            return sb.ToString();
        }

        private static string MapPgType(string csharpType)
        {
            string t = csharpType.ToLowerInvariant();
            if (t.StartsWith("system.")) t = t.Substring("system.".Length);
            switch (t)
            {
                case "int":
                case "int32":
                case "system.int32": return "integer";
                case "long":
                case "int64":
                case "system.int64": return "bigint";
                case "short":
                case "int16":
                case "system.int16": return "smallint";
                case "byte": return "smallint";
                case "sbyte": return "smallint";
                // Unsigned types are stored widened to fit their full range (the write side maps them the same way).
                case "ushort":
                case "uint16":
                case "system.uint16": return "integer";
                case "uint":
                case "uint32":
                case "system.uint32": return "bigint";
                case "ulong":
                case "uint64":
                case "system.uint64": return "numeric";
                case "decimal": return "numeric";
                case "double": return "double precision";
                case "float":
                case "single": return "real";
                case "string": return "text";
                case "char": return "character(1)";
                case "datetime": return "timestamp without time zone";
                case "datetimeoffset": return "timestamp with time zone";
                case "date":
                case "dateonly": return "date";
                case "time":
                case "timeonly": return "time without time zone";
                case "timespan": return "interval";
                case "bool":
                case "boolean": return "boolean";
                case "guid": return "uuid";
                case "byte[]": return "bytea";
                // An `object` property maps to jsonb, matching the migration generator's CSharpTypeMapping, so a
                // baked [TableType] CREATE TABLE and a migration CREATE TABLE for the same column agree.
                case "object":
                case "system.object": return "jsonb";
                default: return "text";
            }
        }

        private static string ToSnakeCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new System.Text.StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0) sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
