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
                // INstanceConstructors is in declaration/metadata order, NOT by arity, so paramCtors[0] may be
                // a convenience overload (e.g. `record T(string Name, int Priority) { T(string name) : this(name, 0) }`).
                // Blindly taking [0] would silently drop members. Pick the widest constructor (the record's primary
                // ctor); if two share the max arity it's genuinely ambiguous — report rather than guess.
                int maxArity = paramCtors.Max(c => c.Parameters.Length);
                var widest = paramCtors.Where(c => c.Parameters.Length == maxArity).ToList();
                if (widest.Count > 1)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.DtoNotMappable, loc, full));
                    return;
                }
                foreach (var p in widest[0].Parameters)
                    members.Add((p.Name, p.Type));
            }
            else
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.DtoNotMappable, loc, full));
                return;
            }

            string id = MapperId(full);

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
                var enumUnderlying = ((INamedTypeSymbol)underlying).EnumUnderlyingType!;
                string eu = enumUnderlying.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                // Enums are stored as their underlying integer, but the unsigned/byte underlyings are stored WIDENED
                // (byte/sbyte->smallint, ushort->integer, uint->bigint, ulong->numeric) and Npgsql has no reader
                // handler to read those back directly — GetFieldValue<ushort>/<uint>/<ulong>/<byte> throws. Read the
                // signed/decimal storage type and narrow (checked) to the underlying, matching ReadScalar and
                // ApplyDbValue and the non-enum unsigned cases below. Other underlyings read directly.
                string readExpr = enumUnderlying.SpecialType switch
                {
                    SpecialType.System_Byte or SpecialType.System_SByte => $"({eu})r.GetFieldValue<short>(o[{index}])",
                    SpecialType.System_UInt16 => $"checked((ushort)r.GetFieldValue<int>(o[{index}]))",
                    SpecialType.System_UInt32 => $"checked((uint)r.GetFieldValue<long>(o[{index}]))",
                    SpecialType.System_UInt64 => $"checked((ulong)r.GetFieldValue<decimal>(o[{index}]))",
                    _ => $"r.GetFieldValue<{eu}>(o[{index}])",
                };
                return $"({guard}) ? default({castType}) : ({castType})({readExpr})";
            }

            // Unsigned columns are stored widened (ushort->int, uint->bigint, ulong->numeric), and byte/sbyte are
            // stored as smallint; Npgsql has no reader handler that narrows those back, so read the signed/decimal
            // storage type and narrow, matching ReadScalar and the write side.
            // checked() so an out-of-range stored value throws (matching the slow ApplyDbValue path and ReadScalar)
            // instead of silently wrapping.
            switch (underlying.SpecialType)
            {
                case SpecialType.System_UInt16:
                    return $"({guard}) ? default({castType}) : ({castType})checked((ushort)r.GetFieldValue<int>(o[{index}]))";
                case SpecialType.System_UInt32:
                    return $"({guard}) ? default({castType}) : ({castType})checked((uint)r.GetFieldValue<long>(o[{index}]))";
                case SpecialType.System_UInt64:
                    return $"({guard}) ? default({castType}) : ({castType})checked((ulong)r.GetFieldValue<decimal>(o[{index}]))";
                case SpecialType.System_Byte:
                    return $"({guard}) ? default({castType}) : ({castType})checked((byte)r.GetFieldValue<short>(o[{index}]))";
                case SpecialType.System_SByte:
                    return $"({guard}) ? default({castType}) : ({castType})checked((sbyte)r.GetFieldValue<short>(o[{index}]))";
            }

            string readType = underlying.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return $"({guard}) ? default({castType}) : ({castType})r.GetFieldValue<{readType}>(o[{index}])";
        }

        /// <summary>Maps a fully-qualified name to a stable identifier used for the mapper method names.
        /// Shared with <see cref="ProcedureGenerator"/> so both sides compute the same id.</summary>
        // The unique mapper method-name id for a DTO type's fully-qualified name. MUST be used by BOTH the mapper
        // definition (here) and the procedure call site (ProcedureGenerator) so they reference the same method.
        internal static string MapperId(string fullyQualifiedName)
            => Sanitize(fullyQualifiedName) + "_" + StableHash(fullyQualifiedName);

        internal static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        // A deterministic (FNV-1a) hash of the full type name, appended to the sanitized mapper id so that two
        // distinct DTO types whose fully-qualified names differ ONLY at a separator (e.g. `A.B.C` vs namespace
        // `A_B` type `C`) — which sanitize to the same identifier — get distinct method names instead of colliding
        // into a duplicate-member (CS0111) compile error in the generated Procedures.g.cs.
        internal static string StableHash(string s)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in s) { hash ^= c; hash *= 16777619; }
                return hash.ToString("x8");
            }
        }
    }
}
