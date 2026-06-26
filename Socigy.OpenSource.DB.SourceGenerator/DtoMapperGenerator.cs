using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Socigy.OpenSource.DB.SourceGenerator
{
    /// <summary>
    /// Emits an AOT-safe materializer for each distinct non-[Table] type used in a procedure's
    /// <c>-- @returns:</c>. Unlike [Table] types (which carry a generated <c>ConvertFrom</c>), these
    /// plain POCO/record types have no generated mapper, so one is emitted here — by column name,
    /// using the same reflection-free <c>GetFieldValue&lt;T&gt;</c> read pattern as the table path.
    /// Everything lands in a single top-level <c>__ProcedureDtoMappers</c> class in the generated
    /// namespace; procedures reference it by simple name.
    /// </summary>
    internal static class DtoMapperGenerator
    {
        /// <summary>Emits the <c>__ProcedureDtoMappers</c> class. <paramref name="dtos"/> is keyed by
        /// fully-qualified type name (dedup key); the value carries the symbol and the first referencing
        /// <c>.sql</c> location for diagnostics.</summary>
        public static void Emit(
            StringBuilder sb,
            IReadOnlyDictionary<string, (INamedTypeSymbol Symbol, Location Location)> dtos,
            SourceProductionContext ctx)
        {
            if (dtos.Count == 0)
                return;

            sb.AppendLine();
            sb.AppendLine("    internal static class __ProcedureDtoMappers");
            sb.AppendLine("    {");
            sb.AppendLine("        private static int __OrdSafe(DbDataReader r, string name)");
            sb.AppendLine("        {");
            sb.AppendLine("            try { return r.GetOrdinal(name); } catch { return -1; }");
            sb.AppendLine("        }");

            foreach (var kvp in dtos)
                EmitOne(sb, kvp.Key, kvp.Value.Symbol, kvp.Value.Location, ctx);

            sb.AppendLine("    }");
        }

        private static void EmitOne(StringBuilder sb, string full, INamedTypeSymbol symbol, Location loc, SourceProductionContext ctx)
        {
            if (symbol.IsAbstract || symbol.TypeKind == TypeKind.Interface || symbol.IsGenericType)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.DtoNotMappable, loc, full));
                return;
            }

            var publicCtors = symbol.InstanceConstructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                .ToList();
            var paramCtors = publicCtors.Where(c => c.Parameters.Length > 0).ToList();
            bool hasParameterless = publicCtors.Any(c => c.Parameters.Length == 0);

            var members = new List<(string Name, ITypeSymbol Type)>();
            bool positional;

            // Prefer a single positional constructor (records); fall back to settable properties; else a
            // lone parameterized constructor. A type with neither cannot be constructed → SCGDB021.
            if (paramCtors.Count == 1 && !hasParameterless)
            {
                positional = true;
                foreach (var p in paramCtors[0].Parameters)
                    members.Add((p.Name, p.Type));
            }
            else if (hasParameterless)
            {
                positional = false;
                foreach (var prop in EnumerateSettableProperties(symbol))
                    members.Add((prop.Name, prop.Type));
            }
            else if (paramCtors.Count >= 1)
            {
                positional = true;
                foreach (var p in paramCtors[0].Parameters)
                    members.Add((p.Name, p.Type));
            }
            else
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.DtoNotMappable, loc, full));
                return;
            }

            string id = Sanitize(full);

            sb.AppendLine();
            sb.Append($"        internal static int[] Ordinals_{id}(DbDataReader r) => new int[] {{ ");
            sb.Append(string.Join(", ", members.Select(m => $"__OrdSafe(r, \"{m.Name}\")")));
            sb.AppendLine(" };");

            sb.AppendLine($"        internal static {full} Map_{id}(DbDataReader r, int[] o)");
            sb.AppendLine("        {");
            if (positional)
            {
                sb.AppendLine($"            return new {full}(");
                for (int i = 0; i < members.Count; i++)
                {
                    string comma = i < members.Count - 1 ? "," : "";
                    sb.AppendLine($"                {ReadExpr(members[i].Type, i)}{comma}");
                }
                sb.AppendLine("            );");
            }
            else
            {
                sb.AppendLine($"            var __x = new {full}();");
                for (int i = 0; i < members.Count; i++)
                    sb.AppendLine($"            __x.{members[i].Name} = {ReadExpr(members[i].Type, i)};");
                sb.AppendLine("            return __x;");
            }
            sb.AppendLine("        }");
        }

        /// <summary>Public, non-static, non-indexer instance properties with a public setter, most-derived first.</summary>
        private static IEnumerable<IPropertySymbol> EnumerateSettableProperties(INamedTypeSymbol type)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (INamedTypeSymbol? t = type; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            {
                foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
                {
                    if (p.IsStatic || p.IsIndexer) continue;
                    if (p.DeclaredAccessibility != Accessibility.Public) continue;
                    if (p.SetMethod == null || p.SetMethod.DeclaredAccessibility != Accessibility.Public) continue;
                    if (seen.Add(p.Name))
                        yield return p;
                }
            }
        }

        /// <summary>
        /// Builds the per-column read expression, mirroring the [Table] path: a missing ordinal (-1) or a
        /// SQL NULL yields <c>default</c>; otherwise a non-boxing <c>GetFieldValue&lt;T&gt;</c> read. Enums
        /// are read through their underlying primitive.
        /// </summary>
        private static string ReadExpr(ITypeSymbol type, int index)
        {
            bool isNullableValue = type is INamedTypeSymbol nt
                && nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
            ITypeSymbol underlying = isNullableValue ? ((INamedTypeSymbol)type).TypeArguments[0] : type;

            string castType = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string guard = $"o[{index}] < 0 || r.IsDBNull(o[{index}])";

            if (underlying.TypeKind == TypeKind.Enum)
            {
                string eu = ((INamedTypeSymbol)underlying).EnumUnderlyingType!
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                return $"({guard}) ? default({castType}) : ({castType})({eu})r.GetFieldValue<{eu}>(o[{index}])";
            }

            string readType = underlying.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return $"({guard}) ? default({castType}) : ({castType})r.GetFieldValue<{readType}>(o[{index}])";
        }

        /// <summary>Maps a fully-qualified name to a stable identifier used for the mapper method names.
        /// Shared with <see cref="ProcedureGenerator"/> so both sides compute the same id.</summary>
        internal static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }
    }
}
