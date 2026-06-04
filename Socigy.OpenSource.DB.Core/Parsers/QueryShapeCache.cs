using System;
using System.Collections.Concurrent;

namespace Socigy.OpenSource.DB.Core.Parsers
{
#nullable enable
    /// <summary>
    /// Process-wide cache of translated SQL fragments keyed by entity type + a structural hash of the
    /// predicate (see <see cref="ExpressionStructure"/>). On a hit the caller skips the LINQ→SQL
    /// translation entirely and only re-binds the parameter values, so steady-state queries pay no
    /// translation cost (and allocate less, since the SQL string and builder scratch are not rebuilt).
    ///
    /// Memory is bounded: one small entry per distinct query shape, populated once and reused for the
    /// process lifetime. New shapes stop being cached once <see cref="MaxEntries"/> is reached.
    /// </summary>
    internal static class QueryShapeCache
    {
        /// <summary>Soft upper bound on cached shapes, guarding against pathological shape explosions.</summary>
        public const int MaxEntries = 8192;

        private static readonly ConcurrentDictionary<(Type Type, long Hash), Entry> _cache = new();

        private readonly struct Entry
        {
            public readonly string Sql;
            public readonly int ParamCount;
            public Entry(string sql, int paramCount) { Sql = sql; ParamCount = paramCount; }
        }

        public static bool TryGet(Type type, long hash, out string sql, out int paramCount)
        {
            if (_cache.TryGetValue((type, hash), out Entry entry))
            {
                sql = entry.Sql;
                paramCount = entry.ParamCount;
                return true;
            }
            sql = string.Empty;
            paramCount = 0;
            return false;
        }

        public static void Add(Type type, long hash, string sql, int paramCount)
        {
            if (_cache.Count >= MaxEntries) return;
            _cache.TryAdd((type, hash), new Entry(sql, paramCount));
        }

        /// <summary>Test/diagnostic hook — clears all cached shapes.</summary>
        public static void Clear() => _cache.Clear();

        /// <summary>Test/diagnostic hook — number of cached shapes.</summary>
        public static int Count => _cache.Count;
    }
#nullable disable
}
