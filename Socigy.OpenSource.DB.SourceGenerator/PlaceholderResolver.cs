using Microsoft.CodeAnalysis;
using Socigy.OpenSource.DB.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Socigy.OpenSource.DB.SourceGenerator
{
    /// <summary>Outcome of expanding <c>{{Type.Property}}</c> placeholders in a SQL body.</summary>
    internal readonly struct PlaceholderResult
    {
        /// <summary>The SQL body with every resolvable placeholder replaced by its quoted column name.</summary>
        public readonly string Sql;

        /// <summary>True when the original body contained at least one <c>{{ … }}</c> token (well-formed or not).</summary>
        public readonly bool AnyPlaceholderSeen;

        public PlaceholderResult(string sql, bool anyPlaceholderSeen)
        {
            Sql = sql;
            AnyPlaceholderSeen = anyPlaceholderSeen;
        }
    }

    /// <summary>
    /// Expands optional <c>{{Type.Property}}</c> placeholders in a procedure's SQL body into the
    /// quoted database column name (e.g. <c>{{UserLogin.Username}}</c> → <c>"username"</c>), resolving
    /// the column name through <see cref="ColumnNaming"/> so it always matches the generated
    /// <c>{Prop}ColumnName</c> constant. Diagnostics are accumulated, not reported, so the caller can
    /// attach the owning <c>.sql</c> file location.
    /// </summary>
    internal static class PlaceholderResolver
    {
        private static readonly string ColumnAttributeFullName = typeof(ColumnAttribute).FullName!;

        // Detects any {{ ... }} token so the "no placeholder" warning (SCGDB003) is not raised for a
        // file that only contains a malformed one.
        private static readonly Regex AnyToken = new(@"\{\{.*?\}\}", RegexOptions.Compiled | RegexOptions.Singleline);

        // Captures a well-formed Type.Property (dotted identifier) placeholder.
        private static readonly Regex StrictToken = new(@"\{\{\s*([\w.]+)\s*\}\}", RegexOptions.Compiled);

        public static PlaceholderResult Resolve(
            string sqlBody,
            Compilation compilation,
            IReadOnlyList<INamedTypeSymbol> allTypes,
            List<(DiagnosticDescriptor Descriptor, object[] Args)> diags)
        {
            if (string.IsNullOrEmpty(sqlBody))
                return new PlaceholderResult(sqlBody, false);

            bool anySeen = AnyToken.IsMatch(sqlBody);
            if (!anySeen)
                return new PlaceholderResult(sqlBody, false);

            string result = StrictToken.Replace(sqlBody, m => Expand(m, compilation, allTypes, diags));
            return new PlaceholderResult(result, true);
        }

        private static string Expand(
            Match match,
            Compilation compilation,
            IReadOnlyList<INamedTypeSymbol> allTypes,
            List<(DiagnosticDescriptor Descriptor, object[] Args)> diags)
        {
            string raw = match.Value;                    // e.g. "{{UserLogin.Username}}"
            string path = match.Groups[1].Value.Trim();  // e.g. "UserLogin.Username"

            // Every dotted segment must be a non-empty identifier.
            string[] segments = path.Split('.');
            if (segments.Length == 0 || segments.Any(s => s.Length == 0))
            {
                diags.Add((Diagnostics.PlaceholderMalformed, new object[] { raw }));
                return raw;
            }

            // {{Type}} — a single segment is a table-name placeholder.
            if (segments.Length == 1)
            {
                var (tableType, tableAmbiguous, tableCandidates) = LookupType(path, compilation, allTypes);
                if (tableAmbiguous)
                {
                    diags.Add((Diagnostics.PlaceholderAmbiguousType, new object[] { raw, path, tableCandidates }));
                    return raw;
                }
                if (tableType == null)
                {
                    diags.Add((Diagnostics.PlaceholderUnknownType, new object[] { raw, path }));
                    return raw;
                }
                return ResolveTableLiteral(tableType, raw, diags);
            }

            // {{Type.Property}} — split at the last dot. If the prefix resolves to a type, this is a
            // column reference; otherwise the prefix is a namespace and the whole path is a
            // fully-qualified table name (e.g. {{MyApp.User}}).
            int lastDot = path.LastIndexOf('.');
            string typeName = path.Substring(0, lastDot);
            string propName = path.Substring(lastDot + 1);

            var (prefixType, prefixAmbiguous, prefixCandidates) = LookupType(typeName, compilation, allTypes);
            if (prefixAmbiguous)
            {
                diags.Add((Diagnostics.PlaceholderAmbiguousType, new object[] { raw, typeName, prefixCandidates }));
                return raw;
            }

            if (prefixType != null)
            {
                if (!IsTable(prefixType))
                {
                    diags.Add((Diagnostics.PlaceholderNotATable, new object[] { raw, prefixType.ToDisplayString() }));
                    return raw;
                }

                IPropertySymbol? prop = FindProperty(prefixType, propName);
                if (prop == null)
                {
                    diags.Add((Diagnostics.PlaceholderUnknownProperty, new object[] { raw, propName, prefixType.ToDisplayString() }));
                    return raw;
                }

                string dbName = ColumnNaming.ResolveDbColumnName(prop, ColumnAttributeFullName);
                return "\"" + dbName + "\"";
            }

            // Prefix is not a type — try the whole path as a fully-qualified table type.
            var (wholeType, wholeAmbiguous, wholeCandidates) = LookupType(path, compilation, allTypes);
            if (wholeAmbiguous)
            {
                diags.Add((Diagnostics.PlaceholderAmbiguousType, new object[] { raw, path, wholeCandidates }));
                return raw;
            }
            if (wholeType == null)
            {
                // Report the column-type name, the most likely intent for a dotted placeholder.
                diags.Add((Diagnostics.PlaceholderUnknownType, new object[] { raw, typeName }));
                return raw;
            }
            return ResolveTableLiteral(wholeType, raw, diags);
        }

        /// <summary>Expands a table-name placeholder to the quoted SQL table name (e.g. <c>"users"</c>).</summary>
        private static string ResolveTableLiteral(
            INamedTypeSymbol type,
            string raw,
            List<(DiagnosticDescriptor Descriptor, object[] Args)> diags)
        {
            if (!IsTable(type))
            {
                diags.Add((Diagnostics.PlaceholderNotATable, new object[] { raw, type.ToDisplayString() }));
                return raw;
            }

            string? tableName = GetTableName(type);
            if (string.IsNullOrEmpty(tableName))
            {
                diags.Add((Diagnostics.PlaceholderNotATable, new object[] { raw, type.ToDisplayString() }));
                return raw;
            }

            return "\"" + tableName + "\"";
        }

        /// <summary>Reads the SQL table name from the type's [Table] / [FlagTable] attribute (first ctor arg).</summary>
        private static string? GetTableName(INamedTypeSymbol type)
        {
            foreach (var attr in type.GetAttributes())
            {
                var name = attr.AttributeClass?.ToDisplayString();
                if ((name == Program.TableAttributeFullName || name == Program.FlagTableAttributeFullName)
                    && attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is string tableName
                    && !string.IsNullOrWhiteSpace(tableName))
                {
                    return tableName;
                }
            }
            return null;
        }

        /// <summary>
        /// Resolves a type by fully-qualified metadata name, then by simple name across the compilation.
        /// Reports nothing; returns whether the lookup was ambiguous so the caller can pick the right diagnostic.
        /// </summary>
        private static (INamedTypeSymbol? Type, bool Ambiguous, string Candidates) LookupType(
            string typeName,
            Compilation compilation,
            IReadOnlyList<INamedTypeSymbol> allTypes)
        {
            var byMetadata = compilation.GetTypeByMetadataName(typeName);
            if (byMetadata != null)
                return (byMetadata, false, "");

            var matches = allTypes.Where(t => t.Name == typeName).ToList();
            if (matches.Count == 1)
                return (matches[0], false, "");
            if (matches.Count == 0)
                return (null, false, "");

            return (null, true, string.Join(", ", matches.Select(t => t.ToDisplayString())));
        }

        private static bool IsTable(INamedTypeSymbol type)
        {
            foreach (var attr in type.GetAttributes())
            {
                var name = attr.AttributeClass?.ToDisplayString();
                if (name == Program.TableAttributeFullName || name == Program.FlagTableAttributeFullName)
                    return true;
            }
            return false;
        }

        private static readonly string IgnoreAttrName = typeof(Socigy.OpenSource.DB.Attributes.IgnoreAttribute).FullName!;
        private static readonly string FlaggedEnumAttrName = typeof(Socigy.OpenSource.DB.Attributes.FlaggedEnumAttribute).FullName!;
        private static readonly string FlaggedEnumTableAttrName = typeof(Socigy.OpenSource.DB.Attributes.FlaggedEnumTableAttribute).FullName!;

        private static IPropertySymbol? FindProperty(INamedTypeSymbol type, string propName)
        {
            for (INamedTypeSymbol? current = type; current != null; current = current.BaseType)
            {
                // Take the MOST-DERIVED declaration of this name (mapped or not) and accept it only if it is a real
                // mapped column — do NOT fall through to a shadowed base declaration. This mirrors the table
                // generator's EnumerateColumnProperties, which dedups by name before filtering, so a `new`-shadowed
                // property marked [Ignore]/get-only in the derived type yields NO column there too. Falling through
                // would resolve {{Derived.Prop}} to the base column the table generator never creates (a runtime
                // "column does not exist"). Static/indexer declarations are skipped (they are never the column).
                var prop = current.GetMembers(propName).OfType<IPropertySymbol>()
                    .FirstOrDefault(p => !p.IsStatic && !p.IsIndexer);
                if (prop != null)
                    return IsMappedColumn(prop) ? prop : null;
            }
            return null;
        }

        private static bool IsMappedColumn(IPropertySymbol p)
        {
            if (p.IsStatic || p.SetMethod == null || p.SetMethod.IsInitOnly)
                return false;
            foreach (var a in p.GetAttributes())
            {
                var n = a.AttributeClass?.ToDisplayString();
                if (n == IgnoreAttrName || n == FlaggedEnumAttrName || n == FlaggedEnumTableAttrName)
                    return false;
            }
            return true;
        }
    }
}
