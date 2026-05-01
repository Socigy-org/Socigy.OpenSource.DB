using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Socigy.OpenSource.DB.SourceGenerator
{
    public class ProcedureInfo
    {
        /// <summary>C# identifier name of the generated static method (derived from file name).</summary>
        public string Name { get; set; } = "";

        /// <summary>Namespace segments from sub-directory structure under Procedures/.</summary>
        public string[] NamespaceSegments { get; set; } = [];

        /// <summary>Return-type annotation from <c>-- @returns: TypeName</c> or null for void.</summary>
        public string? ReturnType { get; set; }

        /// <summary>When true the method returns IAsyncEnumerable&lt;ReturnType&gt;; otherwise Task&lt;bool&gt; (void).</summary>
        public bool ReturnsMany => ReturnType != null;

        /// <summary>Ordered list of parameters parsed from <c>-- @param name: CSharpType</c> lines.</summary>
        public List<ProcedureParam> Params { get; set; } = new();

        /// <summary>Raw SQL body (everything after the header comment block).</summary>
        public string SqlBody { get; set; } = "";
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

            // Derive method name from file name without extension
            string fileNameNoExt = Path.GetFileNameWithoutExtension(filePath);
            info.Name = ToValidIdentifier(fileNameNoExt);

            // Derive namespace segments from subdirectory path relative to Procedures/ root
            if (!string.IsNullOrEmpty(proceduresRootPath))
            {
                string dir = Path.GetDirectoryName(filePath) ?? "";
                string rel = MakeRelative(dir, proceduresRootPath);
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

            foreach (var line in content.Split('\n'))
            {
                string trimmed = line.Trim();

                if (!headerDone && trimmed.StartsWith("--"))
                {
                    string commentBody = trimmed.Substring(2).Trim();

                    if (commentBody.StartsWith("@returns:", StringComparison.OrdinalIgnoreCase))
                    {
                        info.ReturnType = commentBody.Substring("@returns:".Length).Trim();
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

            if (string.IsNullOrWhiteSpace(info.SqlBody))
                return null;

            return info;
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
            // Normalize separators
            path = path.Replace('\\', '/').TrimEnd('/');
            basePath = basePath.Replace('\\', '/').TrimEnd('/');

            if (path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                return path.Substring(basePath.Length).TrimStart('/');

            return "";
        }
    }
}
