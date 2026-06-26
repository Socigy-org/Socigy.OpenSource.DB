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
        /// <summary>snake_case / lower names → PascalCase (e.g. <c>user_id</c> → <c>UserId</c>).</summary>
        public static string ToPascalCase(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            var parts = name.Split(new[] { '_', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return name;

            var sb = new StringBuilder(name.Length);
            foreach (var part in parts)
            {
                sb.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1)
                    sb.Append(part.Substring(1).ToLowerInvariant());
            }
            return sb.ToString();
        }
    }
}
