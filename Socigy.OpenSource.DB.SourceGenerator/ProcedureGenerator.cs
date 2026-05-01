using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Socigy.OpenSource.DB.SourceGenerator
{
    public static class ProcedureGenerator
    {
        public static void Execute(
            SourceProductionContext ctx,
            Compilation compilation,
            ImmutableArray<AdditionalText> sqlFiles)
        {
            if (sqlFiles.IsDefaultOrEmpty)
                return;

            var procedures = new List<ProcedureInfo>();

            foreach (var file in sqlFiles)
            {
                var filePath = file.Path.Replace('\\', '/');
                var procIdx = filePath.IndexOf("/Socigy/Procedures", StringComparison.OrdinalIgnoreCase);
                if (procIdx < 0)
                    continue;

                var proceduresRoot = filePath.Substring(0, procIdx + "/Socigy/Procedures".Length);
                var content = file.GetText(default)?.ToString() ?? "";
                var info = ProcedureParser.Parse(filePath, content, proceduresRoot);
                if (info != null)
                    procedures.Add(info);
            }

            if (procedures.Count == 0)
                return;

            var sb = new StringBuilder();
            sb.AppendLine("#pragma warning disable");
            sb.AppendLine("using System.Data.Common;");
            sb.AppendLine();
            sb.AppendLine($"namespace {compilation.AssemblyName}.Socigy.Generated");
            sb.AppendLine("{");

            EmitGroup(sb, "Procedures", procedures, 1);

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
                sb.AppendLine($"{indent}    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);");
                sb.AppendLine($"{indent}    while (await reader.ReadAsync(cancellationToken))");
                sb.AppendLine($"{indent}        yield return {proc.ReturnType}.ConvertFrom(reader);");
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
                sb.AppendLine($"{indent}    int affected = await cmd.ExecuteNonQueryAsync();");
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
    }
}
