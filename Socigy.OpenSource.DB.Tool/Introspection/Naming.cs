using System;
using System.Linq;
using System.Text;

namespace Socigy.OpenSource.DB.Tool.Introspection
{
    /// <summary>Identifier conversions for DB-first scaffolding. The forward direction (C# → DB column) is
    /// the analyzer's <c>JsonNamingPolicy.SnakeCaseLower</c>; <see cref="ToPascalCase"/> is its practical
    /// inverse so that scaffolding a snake_case DB name and snake-casing it back agree for typical names.</summary>
    internal static class Naming
    {
        /// <summary>snake_case / lower names → a valid PascalCase C# identifier (e.g. <c>user_id</c> → <c>UserId</c>).
        /// Splits on ANY non-alphanumeric separator (not just <c>_ - space</c>) so a DB name containing other
        /// punctuation (a quoted <c>"weird.name"</c>) does not leak an invalid character into the identifier, and
        /// prefixes an underscore when the result would start with a digit (e.g. <c>2fa_enabled</c> → <c>_2faEnabled</c>),
        /// which C# forbids — otherwise the emitted class/property did not compile. The emitter adds a <c>[Column]</c>
        /// override whenever the sanitized name no longer snake_cases back to the DB name, so the round-trip holds.</summary>
        public static string ToPascalCase(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            // Split into runs of letters/digits, dropping every other character.
            var parts = new System.Collections.Generic.List<string>();
            var run = new StringBuilder();
            foreach (char ch in name)
            {
                if (char.IsLetterOrDigit(ch)) run.Append(ch);
                else if (run.Length > 0) { parts.Add(run.ToString()); run.Clear(); }
            }
            if (run.Length > 0) parts.Add(run.ToString());
            if (parts.Count == 0)
                return "_"; // all-punctuation name: emit a placeholder identifier (the [Column] carries the real name)

            var sb = new StringBuilder(name.Length + 1);
            foreach (var part in parts)
            {
                sb.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1)
                    sb.Append(part.Substring(1).ToLowerInvariant());
            }
            if (char.IsDigit(sb[0]))
                sb.Insert(0, '_');
            return sb.ToString();
        }
    }
}
