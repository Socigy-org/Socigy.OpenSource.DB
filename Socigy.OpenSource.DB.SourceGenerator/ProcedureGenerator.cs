using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Socigy.OpenSource.DB.SourceGenerator
{
    public static class ProcedureGenerator
    {
        // Matches a SQL parameter reference (@name) while ignoring the @@ operator family.
        private static readonly Regex SqlParamRef = new(@"(?<!@)@([A-Za-z_]\w*)", RegexOptions.Compiled);

        // C# keyword aliases that never resolve via GetTypeByMetadataName but are valid return types.
        private static readonly HashSet<string> PrimitiveAliases = new(StringComparer.Ordinal)
        {
            "bool", "byte", "sbyte", "char", "decimal", "double", "float",
            "int", "uint", "long", "ulong", "short", "ushort", "string", "object",
        };

        // Framework value types (beyond the primitive aliases) accepted as `-- @returns scalar:` types.
        private static readonly HashSet<string> ScalarFrameworkTypes = new(StringComparer.Ordinal)
        {
            "Guid", "System.Guid",
            "DateTime", "System.DateTime",
            "DateTimeOffset", "System.DateTimeOffset",
            "TimeSpan", "System.TimeSpan",
            "DateOnly", "System.DateOnly",
            "TimeOnly", "System.TimeOnly",
        };

        // Scalar types that implement IConvertible. For these the generated code routes the boxed provider
        // value through Convert.ChangeType, tolerating provider widening (e.g. COUNT(*) returns bigint/long
        // for an `int` return). Non-IConvertible scalars (Guid, DateTimeOffset, …) are cast directly.
        private static readonly HashSet<string> ConvertibleScalarTypes = new(StringComparer.Ordinal)
        {
            "bool", "byte", "sbyte", "char", "decimal", "double", "float",
            "int", "uint", "long", "ulong", "short", "ushort", "string",
            "System.Boolean", "System.Byte", "System.SByte", "System.Char", "System.Decimal",
            "System.Double", "System.Single", "System.Int32", "System.UInt32", "System.Int64",
            "System.UInt64", "System.Int16", "System.UInt16", "System.String",
        };

        public static void Execute(
            SourceProductionContext ctx,
            Compilation compilation,
            ImmutableArray<AdditionalText> sqlFiles)
        {
            if (sqlFiles.IsDefaultOrEmpty)
                return;

            // Built once and shared across every file/placeholder to avoid O(files × types) scans.
            var allTypes = EnumerateNamedTypes(compilation.GlobalNamespace).ToList();

            var procedures = new List<(ProcedureInfo Info, AdditionalText File)>();

            // Distinct non-[Table] DTO return types that need a generated mapper, keyed by fully-qualified
            // name (the dedup key); value is the symbol plus the first referencing .sql location.
            var dtoMap = new Dictionary<string, (INamedTypeSymbol Symbol, Location Location)>(StringComparer.Ordinal);

            foreach (var file in sqlFiles)
            {
                var filePath = file.Path.Replace('\\', '/');
                var procIdx = filePath.IndexOf("/Socigy/Procedures", StringComparison.OrdinalIgnoreCase);
                if (procIdx < 0)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.SqlFileOutsideProcedures, FileLocation(file), file.Path));
                    continue;
                }

                var proceduresRoot = filePath.Substring(0, procIdx + "/Socigy/Procedures".Length);
                var content = file.GetText(default)?.ToString() ?? "";
                var info = ProcedureParser.Parse(filePath, content, proceduresRoot);

                if (info == null || string.IsNullOrWhiteSpace(info.SqlBody))
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.EmptySqlBody, FileLocation(file), file.Path));
                    continue;
                }

                var location = FileLocation(file);

                // SCGDB012 — malformed -- @param lines.
                foreach (var _ in info.MalformedParamLines)
                    ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.MalformedParamLine, location, info.Name));

                // Feature A — expand {{Type.Property}} placeholders BEFORE any parameter analysis.
                var placeholderDiags = new List<(DiagnosticDescriptor Descriptor, object[] Args)>();
                var resolved = PlaceholderResolver.Resolve(info.SqlBody, compilation, allTypes, placeholderDiags);
                info.SqlBody = resolved.Sql;
                foreach (var (descriptor, args) in placeholderDiags)
                    ctx.ReportDiagnostic(Diagnostic.Create(descriptor, location, args));

                // Feature B — warn when the procedure uses no placeholder at all (unless opted out).
                if (!resolved.AnyPlaceholderSeen && !info.SuppressMissingPlaceholderWarning)
                    ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.MissingPlaceholder, location, info.Name));

                // Return-kind diagnostics, scalar validation, and Rows→Dto downgrade (Workstream 3).
                ResolveReturnKind(ctx, info, location, compilation, allTypes, dtoMap);

                // SCGDB009 / SCGDB010 — parameter declaration vs usage, against the FINAL SQL.
                ReportParameterUsage(ctx, info, location);

                procedures.Add((info, file));
            }

            if (procedures.Count == 0)
                return;

            // SCGDB015 — duplicate generated procedures (same name within the same namespace group).
            var deduped = new List<ProcedureInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (info, file) in procedures)
            {
                var key = string.Join(".", info.NamespaceSegments) + "::" + info.Name;
                if (seen.Add(key))
                {
                    deduped.Add(info);
                }
                else
                {
                    var group = info.NamespaceSegments.Length == 0 ? "Procedures" : string.Join(".", info.NamespaceSegments);
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.DuplicateProcedure, FileLocation(file), info.Name, group));
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("#pragma warning disable");
            sb.AppendLine("using System.Data.Common;");
            sb.AppendLine();
            sb.AppendLine($"namespace {compilation.AssemblyName}.Socigy.Generated");
            sb.AppendLine("{");

            EmitGroup(sb, "Procedures", deduped, 1);

            // Mappers for non-[Table] DTO return types, emitted once per distinct type in this namespace.
            DtoMapperGenerator.Emit(sb, dtoMap, ctx);

            sb.AppendLine("}");
            sb.AppendLine("#pragma warning restore");

            ctx.AddSource("Procedures.g.cs", sb.ToString());
        }

        private static void EmitGroup(
            StringBuilder sb,
            string className,
            IEnumerable<ProcedureInfo> procedures,
            int depth)
        {
            string indent = new string(' ', depth * 4);
            sb.AppendLine($"{indent}public static partial class {className}");
            sb.AppendLine($"{indent}{{");

            foreach (var proc in procedures.Where(p => p.NamespaceSegments.Length == 0))
                EmitMethod(sb, proc, depth + 1);

            foreach (var group in procedures
                .Where(p => p.NamespaceSegments.Length > 0)
                .GroupBy(p => p.NamespaceSegments[0]))
            {
                var stripped = group.Select(p => new ProcedureInfo
                {
                    Name = p.Name,
                    NamespaceSegments = p.NamespaceSegments.Skip(1).ToArray(),
                    ReturnType = p.ReturnType,
                    // The return kind and DTO binding are resolved before grouping; they MUST be carried
                    // onto the stripped copy or nested-namespace procedures lose their signature shape.
                    ReturnKind = p.ReturnKind,
                    ReturnTypeIsNullable = p.ReturnTypeIsNullable,
                    DtoFullName = p.DtoFullName,
                    DtoMapperId = p.DtoMapperId,
                    Params = p.Params,
                    SqlBody = p.SqlBody
                });
                EmitGroup(sb, group.Key, stripped, depth + 1);
            }

            sb.AppendLine($"{indent}}}");
        }

        private static void EmitMethod(StringBuilder sb, ProcedureInfo proc, int depth)
        {
            string indent = new string(' ', depth * 4);

            switch (proc.ReturnKind)
            {
                case ProcedureReturnKind.Rows:
                    EmitStreaming(sb, proc, indent, proc.ReturnType!,
                        $"{proc.ReturnType}.GetColumnOrdinals(reader)",
                        $"{proc.ReturnType}.ConvertFrom(reader, __ords)");
                    break;

                case ProcedureReturnKind.Dto:
                    EmitStreaming(sb, proc, indent, proc.DtoFullName!,
                        $"__ProcedureDtoMappers.Ordinals_{proc.DtoMapperId}(reader)",
                        $"__ProcedureDtoMappers.Map_{proc.DtoMapperId}(reader, __ords)");
                    break;

                case ProcedureReturnKind.Scalar:
                    EmitScalar(sb, proc, indent);
                    break;

                case ProcedureReturnKind.AffectedCount:
                    EmitNonQuery(sb, proc, indent, "System.Threading.Tasks.Task<int>", "return affected;");
                    break;

                default: // Void
                    EmitNonQuery(sb, proc, indent, "System.Threading.Tasks.Task<bool>", "return affected >= 0;");
                    break;
            }
        }

        /// <summary>Emits a streaming <c>IAsyncEnumerable&lt;TElement&gt;</c> procedure. The Rows and Dto kinds
        /// share this shape and differ only in how each row is materialized.</summary>
        private static void EmitStreaming(StringBuilder sb, ProcedureInfo proc, string indent,
            string elementType, string ordinalsExpr, string yieldExpr)
        {
            sb.Append($"{indent}public static async System.Collections.Generic.IAsyncEnumerable<{elementType}> {proc.Name}(");
            sb.Append("DbConnection conn");
            foreach (var p in proc.Params)
                sb.Append($", {p.Type} {p.Name}");
            sb.AppendLine($",");
            sb.AppendLine($"{indent}    [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    await using var cmd = conn.CreateCommand();");
            sb.AppendLine($"{indent}    cmd.CommandText = @\"{EscapeVerbatim(proc.SqlBody)}\";");
            EmitParameters(sb, proc.Params, indent);
            sb.AppendLine($"{indent}    await using var __instr = await global::Socigy.OpenSource.DB.Core.Diagnostics.DbDiagnostics.ExecuteReaderAsync(cmd, \"PROC\", ct => cmd.ExecuteReaderAsync(ct), cancellationToken);");
            sb.AppendLine($"{indent}    var reader = __instr.Reader;");
            sb.AppendLine($"{indent}    int[]? __ords = null;");
            sb.AppendLine($"{indent}    while (await __instr.ReadAsync(cancellationToken))");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        __ords ??= {ordinalsExpr};");
            sb.AppendLine($"{indent}        yield return {yieldExpr};");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}}}");
        }

        /// <summary>Emits a scalar-returning procedure (<c>-- @returns scalar: T</c>) backed by ExecuteScalarAsync.</summary>
        private static void EmitScalar(StringBuilder sb, ProcedureInfo proc, string indent)
        {
            string returnType = proc.ReturnType!;
            string underlying = returnType.Trim().TrimEnd('?');

            // Convertible scalars tolerate provider widening (e.g. COUNT(*) → long for an int return). Others
            // (Guid, DateTimeOffset, byte[], …) route through the shared width-tolerant ApplyDbValue, matching the
            // row/aggregate read paths — a direct cast threw InvalidCastException for a DateTimeOffset scalar
            // (a timestamptz result is boxed as a DateTime, and (DateTimeOffset)DateTime is invalid).
            string valueExpr = ConvertibleScalarTypes.Contains(underlying)
                ? $"({underlying})System.Convert.ChangeType(__scalar, typeof({underlying}), System.Globalization.CultureInfo.InvariantCulture)"
                : $"({underlying})global::Socigy.OpenSource.DB.Core.CommandBuilders.ColumnInfo.ApplyDbValue<{underlying}>(__scalar)";

            sb.Append($"{indent}public static async System.Threading.Tasks.Task<{returnType}> {proc.Name}(");
            sb.Append("DbConnection conn");
            foreach (var p in proc.Params)
                sb.Append($", {p.Type} {p.Name}");
            sb.AppendLine(",");
            sb.AppendLine($"{indent}    System.Threading.CancellationToken cancellationToken = default)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    await using var cmd = conn.CreateCommand();");
            sb.AppendLine($"{indent}    cmd.CommandText = @\"{EscapeVerbatim(proc.SqlBody)}\";");
            EmitParameters(sb, proc.Params, indent);
            sb.AppendLine($"{indent}    var __scalar = await global::Socigy.OpenSource.DB.Core.Diagnostics.DbDiagnostics.ExecuteScalarAsync(cmd, \"PROC\", ct => cmd.ExecuteScalarAsync(ct), cancellationToken);");
            sb.AppendLine($"{indent}    if (__scalar is null || __scalar is System.DBNull) return default!;");
            sb.AppendLine($"{indent}    return {valueExpr};");
            sb.AppendLine($"{indent}}}");
        }

        /// <summary>Emits a non-query procedure. Void returns <c>Task&lt;bool&gt;</c> (affected ≥ 0); the
        /// <c>-- @returns affected</c> kind returns <c>Task&lt;int&gt;</c> (the affected-row count).</summary>
        private static void EmitNonQuery(StringBuilder sb, ProcedureInfo proc, string indent, string taskType, string returnStmt)
        {
            sb.Append($"{indent}public static async {taskType} {proc.Name}(");
            sb.Append("DbConnection conn");
            foreach (var p in proc.Params)
                sb.Append($", {p.Type} {p.Name}");
            sb.AppendLine(",");
            sb.AppendLine($"{indent}    System.Threading.CancellationToken cancellationToken = default)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    await using var cmd = conn.CreateCommand();");
            sb.AppendLine($"{indent}    cmd.CommandText = @\"{EscapeVerbatim(proc.SqlBody)}\";");
            EmitParameters(sb, proc.Params, indent);
            sb.AppendLine($"{indent}    int affected = await global::Socigy.OpenSource.DB.Core.Diagnostics.DbDiagnostics.ExecuteNonQueryAsync(cmd, \"PROC\", ct => cmd.ExecuteNonQueryAsync(ct), cancellationToken);");
            sb.AppendLine($"{indent}    {returnStmt}");
            sb.AppendLine($"{indent}}}");
        }

        private static void EmitParameters(StringBuilder sb, List<ProcedureParam> parameters, string indent)
        {
            for (int i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i];
                sb.AppendLine($"{indent}    var __p{i} = cmd.CreateParameter();");
                sb.AppendLine($"{indent}    __p{i}.ParameterName = \"@{p.Name}\";");
                // Normalize the boxed value the same way the insert/update/COPY paths do — the procedure path sets
                // no NpgsqlDbType, so an enum, unsigned int, Kind=Utc DateTime, or offset DateTimeOffset would
                // otherwise throw or silently corrupt when Npgsql infers the wire type.
                sb.AppendLine($"{indent}    object? __v{i} = (object?){p.Name};");
                sb.AppendLine($"{indent}    if (__v{i} != null && __v{i}.GetType().IsEnum)");
                sb.AppendLine($"{indent}        __v{i} = global::System.Convert.ChangeType(__v{i}, global::System.Enum.GetUnderlyingType(__v{i}.GetType()));");
                sb.AppendLine($"{indent}    else if (__v{i} is global::System.DateTime __dt{i} && __dt{i}.Kind == global::System.DateTimeKind.Utc)");
                sb.AppendLine($"{indent}        __v{i} = global::System.DateTime.SpecifyKind(__dt{i}, global::System.DateTimeKind.Unspecified);");
                sb.AppendLine($"{indent}    else if (__v{i} is global::System.DateTimeOffset __dto{i} && __dto{i}.Offset != global::System.TimeSpan.Zero)");
                sb.AppendLine($"{indent}        __v{i} = __dto{i}.ToUniversalTime();");
                sb.AppendLine($"{indent}    else if (__v{i} is ushort __us{i}) __v{i} = (int)__us{i};");
                sb.AppendLine($"{indent}    else if (__v{i} is uint __ui{i}) __v{i} = (long)__ui{i};");
                sb.AppendLine($"{indent}    else if (__v{i} is ulong __ul{i}) __v{i} = (decimal)__ul{i};");
                sb.AppendLine($"{indent}    __p{i}.Value = __v{i} ?? System.DBNull.Value;");
                sb.AppendLine($"{indent}    cmd.Parameters.Add(__p{i});");
            }
        }

        private static string EscapeVerbatim(string sql) => sql.Replace("\"", "\"\"");

        /// <summary>Reports SCGDB009 (declared-but-unused) and SCGDB010 (used-but-undeclared) parameters.</summary>
        private static void ReportParameterUsage(SourceProductionContext ctx, ProcedureInfo info, Location location)
        {
            var declared = new HashSet<string>(info.Params.Select(p => p.Name), StringComparer.Ordinal);
            var used = new HashSet<string>(
                SqlParamRef.Matches(info.SqlBody).Cast<Match>().Select(m => m.Groups[1].Value),
                StringComparer.Ordinal);

            foreach (var name in declared)
            {
                if (!used.Contains(name))
                    ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.ParamDeclaredButUnused, location, name, info.Name));
            }

            foreach (var name in used)
            {
                if (!declared.Contains(name))
                    ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.ParamUsedButUndeclared, location, info.Name, name));
            }
        }

        /// <summary>
        /// Reports return-directive diagnostics and resolves the final <see cref="ProcedureReturnKind"/>.
        /// Scalar types are validated against the supported-scalar set; a provisional Rows type that
        /// resolves to a non-[Table] symbol is downgraded to <see cref="ProcedureReturnKind.Dto"/> and
        /// registered in <paramref name="dtoMap"/> so a mapper is generated for it.
        /// </summary>
        private static void ResolveReturnKind(
            SourceProductionContext ctx,
            ProcedureInfo info,
            Location location,
            Compilation compilation,
            IReadOnlyList<INamedTypeSymbol> allTypes,
            Dictionary<string, (INamedTypeSymbol Symbol, Location Location)> dtoMap)
        {
            if (info.MalformedReturns)
                ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.MalformedReturns, location, info.Name));
            if (info.ConflictingReturns)
                ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.ConflictingReturns, location, info.Name));

            switch (info.ReturnKind)
            {
                case ProcedureReturnKind.Scalar:
                    if (!IsScalarReturnType(info.ReturnType!))
                        ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.ScalarReturnTypeInvalid, location, info.ReturnType, info.Name));
                    break;

                case ProcedureReturnKind.Rows:
                    var symbol = ResolveType(info.ReturnType!, compilation, allTypes);
                    if (symbol == null)
                    {
                        // Unresolvable — preserve the historical SCGDB011 warning.
                        ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.ReturnTypeNotResolvable, location, info.ReturnType, info.Name));
                    }
                    else if (!IsTableType(symbol))
                    {
                        string full = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        info.ReturnKind = ProcedureReturnKind.Dto;
                        info.DtoFullName = full;
                        info.DtoMapperId = DtoMapperGenerator.MapperId(full);
                        if (!dtoMap.ContainsKey(full))
                            dtoMap[full] = (symbol, location);
                    }
                    // else: a genuine [Table] type → leave as Rows.
                    break;
            }
        }

        /// <summary>Whether a <c>-- @returns scalar:</c> type is a supported scalar (primitive/string or a
        /// known framework value type), ignoring a trailing nullable <c>?</c>.</summary>
        private static bool IsScalarReturnType(string returnType)
        {
            string t = returnType.Trim().TrimEnd('?');
            return PrimitiveAliases.Contains(t) || ScalarFrameworkTypes.Contains(t);
        }

        /// <summary>Resolves a type name (fully-qualified metadata name, else unique simple name) to a symbol,
        /// or null when it does not resolve or is ambiguous.</summary>
        private static INamedTypeSymbol? ResolveType(string typeName, Compilation compilation, IReadOnlyList<INamedTypeSymbol> allTypes)
        {
            string trimmed = typeName.Trim().TrimEnd('?');
            if (trimmed.Length == 0 || trimmed.IndexOf('<') >= 0)
                return null;

            var byMetadata = compilation.GetTypeByMetadataName(trimmed);
            if (byMetadata != null)
                return byMetadata;

            int lastDot = trimmed.LastIndexOf('.');
            string simpleName = lastDot >= 0 ? trimmed.Substring(lastDot + 1) : trimmed;
            var matches = allTypes.Where(t => t.Name == simpleName).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        /// <summary>Whether the type carries [Table] or [FlagTable] (and therefore has a generated mapper).</summary>
        private static bool IsTableType(INamedTypeSymbol type)
        {
            foreach (var attr in type.GetAttributes())
            {
                var name = attr.AttributeClass?.ToDisplayString();
                if (name == Program.TableAttributeFullName || name == Program.FlagTableAttributeFullName)
                    return true;
            }
            return false;
        }

        /// <summary>Recursively enumerates every named type declared in the compilation.</summary>
        private static IEnumerable<INamedTypeSymbol> EnumerateNamedTypes(INamespaceSymbol root)
        {
            foreach (var member in root.GetMembers())
            {
                if (member is INamespaceSymbol ns)
                {
                    foreach (var nested in EnumerateNamedTypes(ns))
                        yield return nested;
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    foreach (var nested in EnumerateNestedTypes(type))
                        yield return nested;
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol type)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                yield return nested;
                foreach (var deeper in EnumerateNestedTypes(nested))
                    yield return deeper;
            }
        }

        /// <summary>
        /// Builds a <see cref="Location"/> for an <see cref="AdditionalText"/> (which has no syntax tree),
        /// so diagnostics squiggle the owning <c>.sql</c> file in the IDE and Build Output.
        /// </summary>
        private static Location FileLocation(AdditionalText file)
        {
            var text = file.GetText(default);
            var span = new TextSpan(0, text?.Length ?? 0);
            var lineSpan = text != null
                ? text.Lines.GetLinePositionSpan(span)
                : new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 0));
            return Location.Create(file.Path, span, lineSpan);
        }
    }
}
