using System;
using System.Text;

namespace Socigy.OpenSource.DB.Tool.Structures.Analysis
{
    /// <summary>
    /// Deterministic name derivation shared by generated constraint and index names.
    /// </summary>
    /// <remarks>
    /// Generated names must be identical across processes: a DOWN script's DROP has to match a name emitted
    /// by a different run of the tool, so <see cref="string.GetHashCode()"/> (randomised per process since
    /// .NET Core) cannot be used.
    /// </remarks>
    internal static class StableName
    {
        /// <summary>Deterministic FNV-1a 32-bit hash, rendered as 8 lowercase hex characters.</summary>
        public static string Hash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in value ?? "") { hash ^= c; hash *= 16777619; }
                return hash.ToString("x8");
            }
        }

        /// <summary>
        /// Fits <paramref name="name"/> within <paramref name="maxLength"/> characters, replacing the tail
        /// with a hash of the full name when it is too long.
        /// </summary>
        /// <remarks>
        /// Database engines silently truncate over-long identifiers (PostgreSQL at 63 bytes, MySQL at 64,
        /// SQL Server at 128), which turns two distinct long names into one and makes the second CREATE fail
        /// as "already exists". Truncating here instead keeps the name unique and, being derived from the
        /// full name, still reproducible.
        /// </remarks>
        public static string Truncate(string name, int maxLength)
        {
            if (string.IsNullOrEmpty(name) || maxLength <= 0) return name;

            // Identifier limits are counted in bytes by some engines; names are ASCII in practice (they are
            // built from snake_case identifiers), so the byte count is measured rather than assumed.
            if (Encoding.UTF8.GetByteCount(name) <= maxLength) return name;

            // 9 characters reserved for "_" + the 8-character hash.
            int keep = Math.Max(1, maxLength - 9);
            var head = name.Substring(0, Math.Min(name.Length, keep));
            return $"{head}_{Hash(name)}";
        }
    }
}
