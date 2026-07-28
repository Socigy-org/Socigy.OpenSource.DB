using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool.Generators;
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

            // Composite indexes map to a class-level [Index(nameof(A), nameof(B))]; single-column ones become a
            // property-level [Index] below. Without these, scaffolding silently dropped every index, so the next
            // `generate` produced a migration that removed them from the database.
            foreach (var index in table.Indexes ?? new List<DbIndex>())
                if ((index.Columns?.Count() ?? 0) > 1)
                    sb.AppendLine($"[Index({RenderIndexArguments(table, index, withColumns: true)})]");

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

                // A column can carry more than one index (commonly a plain one plus a partial one), and
                // [Index] is AllowMultiple, so each is emitted on its own line above the property.
                var columnIndexes = (table.Indexes ?? new List<DbIndex>())
                    .Where(i => (i.Columns?.Count() ?? 0) == 1)
                    .Where(i => MatchesColumn(i.Columns.First(), col))
                    .Select(i => RenderIndexArguments(table, i, withColumns: false))
                    .ToList();

                EmitColumn(sb, col, singleColumnUnique.Contains(col.Name) || singleColumnUnique.Contains(col.SourceName),
                           compositePk, columnIndexes);
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Whether an index column reference names <paramref name="col"/>. The reader stores index columns as
        /// PascalCase (== SourceName) while a hand-built <see cref="DbIndex"/> may carry the DB name, so both
        /// are accepted.
        /// </summary>
        private static bool MatchesColumn(string reference, DbColumn col) =>
            string.Equals(reference, col.SourceName, StringComparison.Ordinal) ||
            string.Equals(reference, col.Name, StringComparison.Ordinal);

        /// <summary>
        /// Renders the argument list of an <c>[Index]</c> attribute, omitting anything that is already the
        /// default so the scaffolded model reads like something a person would have written.
        /// </summary>
        /// <param name="withColumns">
        /// True for the class-level composite form, which lists its columns; false for the property-level
        /// form, where the column is the property the attribute sits on.
        /// </param>
        private static string RenderIndexArguments(DbTable table, DbIndex index, bool withColumns)
        {
            var args = new List<string>();

            if (withColumns)
                foreach (var column in index.Columns ?? Enumerable.Empty<string>())
                    args.Add($"nameof({ToPropertyName(table, column)})");

            // The index name only needs stating when it is not the one the tool would derive anyway.
            if (!string.IsNullOrEmpty(index.Name) && index.Name != DeriveIndexName(table, index))
                args.Add($"Name = \"{Escape(index.Name)}\"");

            if (index.IsUnique) args.Add("Unique = true");
            if (!string.IsNullOrEmpty(index.Method)) args.Add($"Method = {MethodConstant(index.Method)}");
            if (!string.IsNullOrEmpty(index.RawMethod)) args.Add($"RawMethod = \"{Escape(index.RawMethod)}\"");
            if (!string.IsNullOrEmpty(index.Where)) args.Add($"Where = \"{Escape(index.Where)}\"");

            AddColumnArray(args, "Include", table, index.IncludeColumns);
            AddColumnArray(args, "DescendingColumns", table, index.DescendingColumns);
            AddColumnArray(args, "NullsFirstColumns", table, index.NullsFirstColumns);
            AddColumnArray(args, "NullsLastColumns", table, index.NullsLastColumns);

            return string.Join(", ", args);
        }

        private static void AddColumnArray(List<string> args, string member, DbTable table, IEnumerable<string> columns)
        {
            var list = (columns ?? Enumerable.Empty<string>()).ToList();
            if (list.Count == 0) return;
            args.Add($"{member} = new[] {{ {string.Join(", ", list.Select(c => $"nameof({ToPropertyName(table, c)})"))} }}");
        }

        /// <summary>
        /// The name the migration generator would derive for this index, used to decide whether the database's
        /// actual name has to be stated explicitly.
        /// </summary>
        private static string DeriveIndexName(DbTable table, DbIndex index)
        {
            var unnamed = new DbIndex
            {
                TableName = index.TableName ?? table.Name,
                Columns = index.Columns,
                IsUnique = index.IsUnique,
                Method = index.Method,
                RawMethod = index.RawMethod,
                Where = index.Where,
                IncludeColumns = index.IncludeColumns,
                DescendingColumns = index.DescendingColumns,
                NullsFirstColumns = index.NullsFirstColumns,
                NullsLastColumns = index.NullsLastColumns,
            };

            var generator = new PostgreSqlGenerator();
            var plan = IndexPlanner.Plan(unnamed, generator.IndexSupport,
                property => ToColumnName(table, property), generator.MaxIdentifierLength);
            return plan.Index?.Name;
        }

        private static string ToPropertyName(DbTable table, string reference) =>
            (table.Columns ?? new List<DbColumn>())
                .FirstOrDefault(c => c.Name == reference || c.SourceName == reference)?.SourceName ?? reference;

        private static string ToColumnName(DbTable table, string reference) =>
            (table.Columns ?? new List<DbColumn>())
                .FirstOrDefault(c => c.Name == reference || c.SourceName == reference)?.Name
            ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(reference);

        private static string MethodConstant(string token) => token switch
        {
            DbIndexMethods.Hash       => "DbIndexMethods.Hash",
            DbIndexMethods.FullText   => "DbIndexMethods.FullText",
            DbIndexMethods.Spatial    => "DbIndexMethods.Spatial",
            DbIndexMethods.Contains   => "DbIndexMethods.Contains",
            DbIndexMethods.BlockRange => "DbIndexMethods.BlockRange",
            _                         => "DbIndexMethods.Default",
        };

        private static void EmitColumn(StringBuilder sb, DbColumn col, bool isUnique, bool compositePk,
                                       IReadOnlyList<string> indexArguments)
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

            // Separate lines: [Index] is AllowMultiple, and folding several of them into one bracket list
            // would not compile.
            foreach (var args in indexArguments ?? Array.Empty<string>())
                sb.AppendLine(args.Length == 0 ? "    [Index]" : $"    [Index({args})]");

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
