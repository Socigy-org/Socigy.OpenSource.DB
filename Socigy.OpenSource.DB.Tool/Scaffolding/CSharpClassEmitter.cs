using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Socigy.OpenSource.DB.Tool.Introspection;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;

namespace Socigy.OpenSource.DB.Tool.Scaffolding
{
    /// <summary>
    /// Emits annotated <c>[Table]</c> partial classes from a <see cref="DbSchema"/> (DB-first scaffolding).
    /// The attributes mirror exactly what <c>AssemblyAnalyzer</c> reads back, so recompiling the output and
    /// running <c>generate</c> against it reproduces the same schema (a stable round-trip).
    /// </summary>
    internal static class CSharpClassEmitter
    {
        // Reference types need a non-null initializer to satisfy the nullable context.
        private static readonly HashSet<string> ReferenceTypes = new(StringComparer.Ordinal) { "string", "byte[]" };

        /// <summary>Emits one <c>{ClassName}.cs</c> per table. Returns file name → source text.</summary>
        public static IReadOnlyDictionary<string, string> Emit(DbSchema schema, string @namespace)
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var table in schema.Tables)
                files[table.SourceName + ".cs"] = EmitTable(table, @namespace);
            return files;
        }

        private static string EmitTable(DbTable table, string ns)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using Socigy.OpenSource.DB.Attributes;");
            sb.AppendLine("using System;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns};");
            sb.AppendLine();
            sb.AppendLine($"[Table(\"{table.Name}\")]");

            foreach (var fk in (table.Constraints ?? new List<DbConstraint>())
                     .Where(c => c.Type == DbConstraint.Types.ForeignKey))
                sb.AppendLine(EmitForeignKeyAttribute(fk));

            sb.AppendLine($"public partial class {table.SourceName}");
            sb.AppendLine("{");

            bool first = true;
            foreach (var col in table.Columns ?? new List<DbColumn>())
            {
                if (!first) sb.AppendLine();
                first = false;
                EmitColumn(sb, col);
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void EmitColumn(StringBuilder sb, DbColumn col)
        {
            var attrs = new List<string>();
            if (col.IsPrimaryKey == true) attrs.Add("PrimaryKey");
            if (col.IsAutoIncrement == true) attrs.Add("AutoIncrement");
            if (col.IsJsonColumn == true) attrs.Add("RawJsonColumn");
            if (col.MaxLength.HasValue) attrs.Add($"StringLength({col.MaxLength.Value})");
            if (!string.IsNullOrEmpty(col.DefaultValue)) attrs.Add($"Default(\"{Escape(col.DefaultValue)}\")");

            // [Column] only when the property name doesn't snake_case back to the DB name (matches the analyzer).
            string expectedDbName = JsonNamingPolicy.SnakeCaseLower.ConvertName(col.SourceName);
            if (!string.Equals(expectedDbName, col.Name, StringComparison.Ordinal))
                attrs.Add($"Column(\"{Escape(col.Name)}\")");

            if (attrs.Count > 0)
                sb.AppendLine($"    [{string.Join(", ", attrs)}]");

            string type = col.DotnetType + (col.Nullable == true ? "?" : "");
            string initializer = (col.Nullable != true && ReferenceTypes.Contains(col.DotnetType)) ? " = null!;" : "";
            sb.AppendLine($"    public {type} {col.SourceName} {{ get; set; }}{initializer}");
        }

        private static string EmitForeignKeyAttribute(DbConstraint fk)
        {
            string keys = string.Join(", ", (fk.Columns ?? Enumerable.Empty<string>()).Select(c => $"nameof({c})"));
            string targetKeys = string.Join(", ", (fk.TargetColumns ?? Enumerable.Empty<string>())
                .Select(c => $"nameof({fk.TargetTable}.{c})"));

            var parts = new List<string> { $"typeof({fk.TargetTable})" };
            if (!string.IsNullOrEmpty(keys)) parts.Add($"Keys = [{keys}]");
            if (!string.IsNullOrEmpty(targetKeys)) parts.Add($"TargetKeys = [{targetKeys}]");

            var sb = new StringBuilder("[ForeignKey(");
            sb.Append(parts[0]);
            for (int i = 1; i < parts.Count; i++)
                sb.Append(", ").Append(parts[i]);
            sb.Append(")]");
            return sb.ToString();
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
