using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Socigy.OpenSource.DB.SourceGenerator
{
    /// <summary>
    /// How a procedure's <c>-- @returns</c> header maps to the generated method's signature.
    /// <list type="bullet">
    /// <item><see cref="Void"/> — no <c>@returns</c>; emits <c>Task&lt;bool&gt;</c>.</item>
    /// <item><see cref="AffectedCount"/> — <c>@returns affected</c>; emits <c>Task&lt;int&gt;</c>.</item>
    /// <item><see cref="Scalar"/> — <c>@returns scalar: T</c>; emits <c>Task&lt;T&gt;</c>.</item>
    /// <item><see cref="Rows"/> — <c>@returns: SomeTable</c>; emits <c>IAsyncEnumerable&lt;SomeTable&gt;</c>.</item>
    /// <item><see cref="Dto"/> — <c>@returns: SomePoco</c> (non-[Table]); emits <c>IAsyncEnumerable&lt;SomePoco&gt;</c>
    /// with a generator-emitted mapper. The parser cannot tell Rows from Dto (only the generator has the
    /// <c>Compilation</c>), so it records <see cref="Rows"/> provisionally and the generator downgrades.</item>
    /// </list>
    /// </summary>
    public enum ProcedureReturnKind { Void, AffectedCount, Scalar, Rows, Dto }

    public class ProcedureInfo
    {
        /// <summary>C# identifier name of the generated static method (derived from file name).</summary>
        public string Name { get; set; } = "";

        /// <summary>Namespace segments from sub-directory structure under Procedures/.</summary>
        public string[] NamespaceSegments { get; set; } = [];

        /// <summary>Return-type annotation from <c>-- @returns[ scalar]: TypeName</c> or null for void/affected.</summary>
        public string? ReturnType { get; set; }

        /// <summary>How <see cref="ReturnType"/> is materialized. See <see cref="ProcedureReturnKind"/>.</summary>
        public ProcedureReturnKind ReturnKind { get; set; } = ProcedureReturnKind.Void;

        /// <summary>True when the scalar/return annotation ended in <c>?</c> (drives NULL handling).</summary>
        public bool ReturnTypeIsNullable { get; set; }

        /// <summary>True when more than one <c>-- @returns</c> directive appeared (first wins; drives SCGDB020).</summary>
        public bool ConflictingReturns { get; set; }

        /// <summary>True when a <c>-- @returns</c> directive could not be parsed (drives SCGDB022).</summary>
        public bool MalformedReturns { get; set; }

        /// <summary>Fully-qualified name of the non-[Table] DTO return type. Set by the generator for <see cref="ProcedureReturnKind.Dto"/>.</summary>
        public string? DtoFullName { get; set; }

        /// <summary>Sanitized identifier keying the generated DTO mapper. Set by the generator for <see cref="ProcedureReturnKind.Dto"/>.</summary>
        public string? DtoMapperId { get; set; }

        /// <summary>When true the method returns IAsyncEnumerable&lt;ReturnType&gt; (a row stream or a DTO stream).</summary>
        public bool ReturnsMany => ReturnKind == ProcedureReturnKind.Rows || ReturnKind == ProcedureReturnKind.Dto;

        /// <summary>Ordered list of parameters parsed from <c>-- @param name: CSharpType</c> lines.</summary>
        public List<ProcedureParam> Params { get; set; } = new();

        /// <summary>Raw SQL body (everything after the header comment block).</summary>
        public string SqlBody { get; set; } = "";

        /// <summary>True when the header contains a <c>-- @ignore warning</c> directive (suppresses SCGDB003).</summary>
        public bool SuppressMissingPlaceholderWarning { get; set; }

        /// <summary>Optional message following <c>-- @ignore warning:</c> documenting why the file opts out.</summary>
        public string? SuppressionMessage { get; set; }

        /// <summary>Raw <c>-- @param</c> lines that could not be parsed (drives SCGDB012).</summary>
        public List<string> MalformedParamLines { get; set; } = new();
    }

    public class ProcedureParam
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
    }

    public static class ProcedureParser
    {
        /// <summary>
        /// Parses a SQL file and extracts procedure metadata from header comments.
        /// The <paramref name="proceduresRootPath"/> is the absolute path to the Procedures/ root.
        /// The <paramref name="filePath"/> is the absolute path to the SQL file.
        /// </summary>
        public static ProcedureInfo? Parse(string filePath, string content, string? proceduresRootPath)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            var info = new ProcedureInfo();

            string fileNameNoExt = Path.GetFileNameWithoutExtension(filePath);
            info.Name = ToValidIdentifier(fileNameNoExt);

            // Derive namespace segments from subdirectory path relative to Procedures/ root
            if (!string.IsNullOrEmpty(proceduresRootPath))
            {
                string dir = Path.GetDirectoryName(filePath) ?? "";
                string rel = MakeRelative(dir, proceduresRootPath!);
                if (!string.IsNullOrEmpty(rel))
                {
                    info.NamespaceSegments = rel
                        .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(ToValidIdentifier)
                        .ToArray();
                }
            }

            // Parse header comment lines
            var sqlLines = new List<string>();
            bool headerDone = false;
            bool sawReturns = false;

            foreach (var line in content.Split('\n'))
            {
                string trimmed = line.Trim();

                if (!headerDone && trimmed.StartsWith("--"))
                {
                    string commentBody = trimmed.Substring(2).Trim();

                    if (commentBody.StartsWith("@returns", StringComparison.OrdinalIgnoreCase))
                    {
                        // First @returns wins; a later one is a conflict (SCGDB020), not an override.
                        if (sawReturns)
                        {
                            info.ConflictingReturns = true;
                        }
                        else
                        {
                            sawReturns = true;
                            ParseReturns(info, commentBody.Substring("@returns".Length));
                        }
                    }
                    else if (commentBody.StartsWith("@param ", StringComparison.OrdinalIgnoreCase))
                    {
                        string rest = commentBody.Substring("@param ".Length).Trim();
                        int colon = rest.IndexOf(':');
                        if (colon > 0)
                        {
                            info.Params.Add(new ProcedureParam
                            {
                                Name = rest.Substring(0, colon).Trim(),
                                Type = rest.Substring(colon + 1).Trim()
                            });
                        }
                        else
                        {
                            info.MalformedParamLines.Add(trimmed);
                        }
                    }
                    else if (commentBody.StartsWith("@ignore warning", StringComparison.OrdinalIgnoreCase))
                    {
                        info.SuppressMissingPlaceholderWarning = true;
                        string rest = commentBody.Substring("@ignore warning".Length);
                        int idx = rest.IndexOf(':');
                        if (idx >= 0)
                            info.SuppressionMessage = rest.Substring(idx + 1).Trim();
                    }
                    // Skip non-directive comment lines (descriptions etc.)
                }
                else
                {
                    headerDone = true;
                    sqlLines.Add(line.TrimEnd('\r'));
                }
            }

            info.SqlBody = string.Join("\n", sqlLines).Trim();

            // An empty body is returned (not nulled) so the generator can surface SCGDB014;
            // a totally empty file is already handled by the IsNullOrWhiteSpace(content) guard above.
            return info;
        }

        /// <summary>
        /// Parses the text after the <c>@returns</c> keyword into a <see cref="ProcedureReturnKind"/>.
        /// Recognized forms (longest-prefix first):
        /// <c>@returns affected</c>, <c>@returns scalar: T</c>, <c>@returns: T</c>. Anything else sets
        /// <see cref="ProcedureInfo.MalformedReturns"/>.
        /// </summary>
        private static void ParseReturns(ProcedureInfo info, string after)
        {
            string afterTrim = after.TrimStart();

            if (after.StartsWith(":"))
            {
                // @returns: TypeName — row stream ([Table]) or DTO; the generator decides which.
                string type = after.Substring(1).Trim();
                if (type.Length == 0)
                {
                    info.MalformedReturns = true;
                    return;
                }
                info.ReturnType = type;
                info.ReturnTypeIsNullable = type.EndsWith("?");
                info.ReturnKind = ProcedureReturnKind.Rows; // provisional
            }
            else if (afterTrim.StartsWith("scalar", StringComparison.OrdinalIgnoreCase))
            {
                string afterScalar = afterTrim.Substring("scalar".Length);
                int colon = afterScalar.IndexOf(':');
                string type = colon >= 0 ? afterScalar.Substring(colon + 1).Trim() : "";
                if (colon < 0 || type.Length == 0)
                {
                    info.MalformedReturns = true;
                    return;
                }
                info.ReturnType = type;
                info.ReturnTypeIsNullable = type.EndsWith("?");
                info.ReturnKind = ProcedureReturnKind.Scalar;
            }
            else if (afterTrim.StartsWith("affected", StringComparison.OrdinalIgnoreCase))
            {
                info.ReturnKind = ProcedureReturnKind.AffectedCount;
            }
            else
            {
                info.MalformedReturns = true;
            }
        }

        private static string ToValidIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var result = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                    result.Append(c);
                else if (result.Length > 0)
                    result.Append('_');
            }
            if (result.Length == 0 || char.IsDigit(result[0]))
                result.Insert(0, '_');
            return result.ToString();
        }

        private static string MakeRelative(string path, string basePath)
        {
            path = path.Replace('\\', '/').TrimEnd('/');
            basePath = basePath.Replace('\\', '/').TrimEnd('/');

            if (path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                return path.Substring(basePath.Length).TrimStart('/');

            return "";
        }
    }
}
