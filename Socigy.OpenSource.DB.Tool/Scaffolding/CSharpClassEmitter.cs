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

            // Composite (multi-column) UNIQUE constraints map to a class-level [Unique(nameof(A), nameof(B))]
            // (single-column uniques are emitted as a property-level [Unique] below). The constraint columns are DB
            // names; map them to the property (SourceName) the analyzer reads back via nameof.
            foreach (var uq in (table.Constraints ?? new List<DbConstraint>())
                     .Where(c => c.Type == DbConstraint.Types.Unique))
            {
                var cols = (uq.Columns ?? Enumerable.Empty<string>()).ToList();
                if (cols.Count < 2) continue;
                // Constraint columns come from the schema reader as PascalCase (== SourceName), but a hand-built
                // DbConstraint may carry the DB (snake) name; match either so the nameof() resolves.
                var propNames = cols.Select(dbName =>
                    (table.Columns ?? new List<DbColumn>())
                        .FirstOrDefault(c => c.Name == dbName || c.SourceName == dbName)?.SourceName ?? dbName);
                sb.AppendLine($"[Unique({string.Join(", ", propNames.Select(p => $"nameof({p})"))})]");
            }

            sb.AppendLine($"public partial class {table.SourceName}");
            sb.AppendLine("{");

            // Single-column UNIQUE constraints map to a property-level [Unique] (the form AssemblyAnalyzer reads
            // back). Composite uniques are emitted as a class-level [Unique(...)] above. Without these a scaffolded
            // class dropped every UNIQUE constraint, so the next `generate` emitted a DROP CONSTRAINT — silently
            // losing the uniqueness guarantee on a scaffold→migrate round-trip.
            var singleColumnUnique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in table.Constraints ?? new List<DbConstraint>())
            {
                if (c.Type != DbConstraint.Types.Unique || c.Columns == null) continue;
                var cols = c.Columns.ToList();
                if (cols.Count == 1) singleColumnUnique.Add(cols[0]);
            }
            // The reader stores unique columns as PascalCase (== SourceName) while col.Name is the DB (snake) name,
            // so matching only col.Name dropped every single-column [Unique] (the next generate then DROPped the
            // constraint, silently losing uniqueness). Match against both names below.

            // A COMPOSITE primary key carries the key position via [PrimaryKey(order)] so a key whose column order
            // differs from the table's column order round-trips; a single-column PK stays a bare [PrimaryKey].
            bool compositePk = (table.Columns ?? new List<DbColumn>()).Count(c => c.IsPrimaryKey == true) > 1;

            bool first = true;
            foreach (var col in table.Columns ?? new List<DbColumn>())
            {
                if (!first) sb.AppendLine();
                first = false;
                EmitColumn(sb, col, singleColumnUnique.Contains(col.Name) || singleColumnUnique.Contains(col.SourceName), compositePk);
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void EmitColumn(StringBuilder sb, DbColumn col, bool isUnique, bool compositePk)
        {
            var attrs = new List<string>();
            if (col.IsPrimaryKey == true)
                attrs.Add(compositePk && col.PrimaryKeyOrder.HasValue ? $"PrimaryKey({col.PrimaryKeyOrder.Value})" : "PrimaryKey");
            if (isUnique) attrs.Add("Unique");
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
            // The referential actions (ON DELETE / ON UPDATE) were silently dropped, so a scaffolded CASCADE / SET
            // NULL FK regenerated WITHOUT the action — the cascade/null behavior was lost — and an action-bearing FK
            // showed a spurious DROP+ADD on every regenerate. Emit them so the round-trip is faithful.
            string? onDelete = ForeignKeyActionExpr(fk.OnDelete);
            string? onUpdate = ForeignKeyActionExpr(fk.OnUpdate);
            if (onDelete != null) parts.Add($"OnDelete = {onDelete}");
            if (onUpdate != null) parts.Add($"OnUpdate = {onUpdate}");

            var sb = new StringBuilder("[ForeignKey(");
            sb.Append(parts[0]);
            for (int i = 1; i < parts.Count; i++)
                sb.Append(", ").Append(parts[i]);
            sb.Append(")]");
            return sb.ToString();
        }

        // Maps a stored referential-action token back to the readable DbValues constant the analyzer reads. Returns
        // null for an unknown/absent action so the attribute simply omits it (the FK then defaults to NO ACTION).
        private static string? ForeignKeyActionExpr(string? token) => token switch
        {
            Socigy.OpenSource.DB.Attributes.DbValues.ForeignKey.Cascade => "DbValues.ForeignKey.Cascade",
            Socigy.OpenSource.DB.Attributes.DbValues.ForeignKey.SetNull => "DbValues.ForeignKey.SetNull",
            Socigy.OpenSource.DB.Attributes.DbValues.ForeignKey.SetDefault => "DbValues.ForeignKey.SetDefault",
            Socigy.OpenSource.DB.Attributes.DbValues.ForeignKey.Restrict => "DbValues.ForeignKey.Restrict",
            Socigy.OpenSource.DB.Attributes.DbValues.ForeignKey.NoAction => "DbValues.ForeignKey.NoAction",
            _ => null,
        };

        // JSON string escaping covers control characters (newline, tab, etc.) too, so a default value that
        // contains them produces a valid C# string literal rather than broken source.
        private static string Escape(string s)
        {
            string json = JsonSerializer.Serialize(s);
            return json.Substring(1, json.Length - 2); // strip the surrounding quotes JsonSerializer adds
        }
    }
}
