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

                // SCGDB011 — unresolvable -- @returns type.
                if (!string.IsNullOrWhiteSpace(info.ReturnType) && !IsResolvableReturnType(info.ReturnType!, compilation, allTypes))
                    ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.ReturnTypeNotResolvable, location, info.ReturnType, info.Name));

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

            if (proc.ReturnsMany)
            {
                sb.Append($"{indent}public static async System.Collections.Generic.IAsyncEnumerable<{proc.ReturnType}> {proc.Name}(");
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
                sb.AppendLine($"{indent}        __ords ??= {proc.ReturnType}.GetColumnOrdinals(reader);");
                sb.AppendLine($"{indent}        yield return {proc.ReturnType}.ConvertFrom(reader, __ords);");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine($"{indent}}}");
            }
            else
            {
                sb.Append($"{indent}public static async System.Threading.Tasks.Task<bool> {proc.Name}(");
                sb.Append("DbConnection conn");
                foreach (var p in proc.Params)
                    sb.Append($", {p.Type} {p.Name}");
                sb.AppendLine(")");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    await using var cmd = conn.CreateCommand();");
                sb.AppendLine($"{indent}    cmd.CommandText = @\"{EscapeVerbatim(proc.SqlBody)}\";");
                EmitParameters(sb, proc.Params, indent);
                sb.AppendLine($"{indent}    int affected = await global::Socigy.OpenSource.DB.Core.Diagnostics.DbDiagnostics.ExecuteNonQueryAsync(cmd, \"PROC\", ct => cmd.ExecuteNonQueryAsync(ct));");
                sb.AppendLine($"{indent}    return affected >= 0;");
                sb.AppendLine($"{indent}}}");
            }
        }

        private static void EmitParameters(StringBuilder sb, List<ProcedureParam> parameters, string indent)
        {
            for (int i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i];
                sb.AppendLine($"{indent}    var __p{i} = cmd.CreateParameter();");
                sb.AppendLine($"{indent}    __p{i}.ParameterName = \"@{p.Name}\";");
                sb.AppendLine($"{indent}    __p{i}.Value = (object?){p.Name} ?? System.DBNull.Value;");
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
        /// Conservative check that an <c>-- @returns</c> type can be resolved. Generic types, nullable
        /// types and C# primitive aliases are always treated as resolvable to avoid false positives.
        /// </summary>
        private static bool IsResolvableReturnType(string returnType, Compilation compilation, IReadOnlyList<INamedTypeSymbol> allTypes)
        {
            string trimmed = returnType.Trim().TrimEnd('?');
            if (trimmed.Length == 0 || trimmed.IndexOf('<') >= 0 || PrimitiveAliases.Contains(trimmed))
                return true;

            if (compilation.GetTypeByMetadataName(trimmed) != null)
                return true;

            int lastDot = trimmed.LastIndexOf('.');
            string simpleName = lastDot >= 0 ? trimmed.Substring(lastDot + 1) : trimmed;
            return allTypes.Any(t => t.Name == simpleName);
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
