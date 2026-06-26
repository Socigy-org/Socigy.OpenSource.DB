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
            // A class may match more than one collected provider (e.g. [Table] + [TableType]); process once.
            var processed = new HashSet<string>();
            foreach (var table in tables)
            {
                var semanticModel = compilation.GetSemanticModel(table.SyntaxTree);
                if (semanticModel.GetDeclaredSymbol(table) is not INamedTypeSymbol tableSymbolInfo || tableSymbolInfo.IsStatic)
                    continue;

                if (!processed.Add(tableSymbolInfo.ToDisplayString()))
                    continue;

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
                        DatabaseName = ColumnNaming.ResolveDbColumnName(symbolInfo, ColumnAttributeFullName)
                    };

                    if (member.AttributeLists.Count > 0)
                    {
                        var attrs = symbolInfo.GetAttributes();

                        var columnAttribute = attrs.FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == ColumnAttributeFullName);
                        // SCGDB018 — an explicit but empty [Column("")] name; ColumnNaming already fell back to snake_case.
                        if (columnAttribute != null &&
                            columnAttribute.ConstructorArguments.Length > 0 &&
                            columnAttribute.ConstructorArguments[0].Value is string colNameArg &&
                            string.IsNullOrWhiteSpace(colNameArg))
                            ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.EmptyColumnName, symbolInfo.Locations.FirstOrDefault(), symbolInfo.Name));

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
                        if (columnInfo.IsEncrypted && (columnInfo.IsJsonColumn || !string.IsNullOrEmpty(columnInfo.Converter)))
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
                else if (!tableColNameClassTemplate.Columns.Any(c => c.IsPrimaryKey))
                    ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.TableNoPrimaryKey, tableSymbolInfo.Locations.FirstOrDefault(), tableSymbolInfo.Name));

                // SCGDB024 — two properties resolving to the same DB column name would emit colliding SQL.
                var seenColumnNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var col in tableColNameClassTemplate.Columns)
                    if (!seenColumnNames.Add(col.DatabaseName))
                        ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.DuplicateColumnName, tableSymbolInfo.Locations.FirstOrDefault(), tableSymbolInfo.Name, col.DatabaseName));

                ctx.AddSource($"{tableColNameClassTemplate.ClassName}.table.g.cs", tableColNameClassTemplate.TransformText());
                ctx.AddSource($"{tableColNameClassTemplate.ClassName}SyntaxMethods.table.g.cs", tableSyntaxTemplate.TransformText());

                if (isTableType)
                    ctx.AddSource(
                        $"{tableColNameClassTemplate.ClassName}.tabletype.g.cs",
                        EmitTableTypePartial(tableColNameClassTemplate.Namespace, tableColNameClassTemplate.ClassName, tableColNameClassTemplate.Columns));
            }

            // Once per assembly that declares at least one [Table]/[TableType]: install the Npgsql binary-COPY
            // bridge into the provider-agnostic Core, enabling BulkCopy / DynamicTable.InsertMultipleCopyAsync.
            if (processed.Count > 0)
                ctx.AddSource("__SocigyBulkCopyBridge.g.cs", BulkCopyBridgeSource);
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
                    var actualType = global::System.Nullable.GetUnderlyingType(col.ClrType) ?? col.ClrType;
                    if (actualType.IsEnum)
                        value = global::System.Convert.ChangeType(value, global::System.Enum.GetUnderlyingType(actualType));
                    await importer.WriteAsync(value, GetDbType(col.ClrType), cancellationToken).ConfigureAwait(false);
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
            => ""CREATE TABLE "" + (ifNotExists ? ""IF NOT EXISTS "" : """") + ""\"""" + tableName.Replace(""\"""", ""\""\"""") + ""\"" ({columnDefs})"";

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
                    pgType = baseType.ToLowerInvariant() switch
                    {
                        "short" or "int16" or "system.int16" => "smallserial",
                        "long" or "int64" or "system.int64" => "bigserial",
                        _ => "serial",
                    };
                    serial = true;
                }
                else if (col.IsEncrypted)
                    pgType = "bytea";
                else if (col.IsJsonColumn)
                    pgType = "jsonb";
                else
                    pgType = MapPgType(baseType);

                bool isReference = baseType == "string" || baseType == "byte[]";
                bool nullable = col.Type.EndsWith("?") || isReference;
                bool notNull = !serial && (col.IsPrimaryKey || !nullable);

                // Quotes are doubled so the result is a valid C# string literal segment.
                var def = "\\\"" + col.DatabaseName + "\\\" " + pgType;
                if (notNull) def += " NOT NULL";
                parts.Add(def);
            }

            var pkCols = columns.Where(c => c.IsPrimaryKey).ToList();
            if (pkCols.Count > 0)
                parts.Add("PRIMARY KEY (" + string.Join(", ", pkCols.Select(c => "\\\"" + c.DatabaseName + "\\\"")) + ")");

            return string.Join(", ", parts);
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
