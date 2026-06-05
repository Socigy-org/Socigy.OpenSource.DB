using System;
using System.Collections.Concurrent;

namespace Socigy.OpenSource.DB.Core.Parsers
{
#nullable enable
    /// <summary>One parameter's replay recipe: where to find its source in the tree, and how to transform it.</summary>
    internal readonly struct ParamSlot
    {
        public readonly int[] Path;
        public readonly ParamTransform Transform;
        public readonly Type? ArrayElementType;
        public ParamSlot(int[] path, ParamTransform transform, Type? arrayElementType)
        {
            Path = path;
            Transform = transform;
            ArrayElementType = arrayElementType;
        }
    }

    /// <summary>A cached query: the full SQL text plus an ordered plan to re-bind parameters on a hit.</summary>
    internal readonly struct CompiledQuery
    {
        public readonly string Sql;
        public readonly ParamSlot[] Plan;
        public CompiledQuery(string sql, ParamSlot[] plan) { Sql = sql; Plan = plan; }
    }

    /// <summary>
    /// Process-wide cache of fully-translated queries keyed by entity type + a structural hash of the
    /// predicate (see <see cref="ExpressionStructure"/>). On a hit the caller skips the visitor, the
    /// string assembly, and the tree-walk entirely — it sets the cached SQL and replays the parameter
    /// plan (navigate to each source sub-expression, evaluate, transform, bind).
    ///
    /// Memory is bounded: one entry per distinct query shape, populated once and reused for the process
    /// lifetime. New shapes stop being cached once <see cref="MaxEntries"/> is reached.
    /// </summary>
    internal static class QueryShapeCache
    {
        /// <summary>Soft upper bound on cached shapes, guarding against pathological shape explosions.</summary>
        public const int MaxEntries = 8192;

        private static readonly ConcurrentDictionary<(Type Type, long Hash), CompiledQuery> _cache = new();

        public static bool TryGet(Type type, long hash, out CompiledQuery query)
            => _cache.TryGetValue((type, hash), out query);

        public static void Add(Type type, long hash, CompiledQuery query)
        {
            if (_cache.Count >= MaxEntries) return;
            _cache.TryAdd((type, hash), query);
        }

        /// <summary>Test/diagnostic hook — clears all cached shapes.</summary>
        public static void Clear() => _cache.Clear();

        /// <summary>Test/diagnostic hook — number of cached shapes.</summary>
        public static int Count => _cache.Count;
    }
#nullable disable
}
