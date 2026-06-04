using System;
using System.Linq.Expressions;

namespace Socigy.OpenSource.DB.Core.Parsers
{
#nullable enable
    /// <summary>
    /// Computes an allocation-free, value-independent structural hash of a predicate expression, used as
    /// the key for the <see cref="QueryShapeCache"/>.
    ///
    /// The hash captures everything that influences the generated SQL — node types, operators, column
    /// member names, method/declaring-type identities, and whether a constant is literal <c>null</c> —
    /// while collapsing every value-producing sub-tree (closures, captured variables, constants) to a
    /// single token. So <c>x.Age &lt; a</c> and <c>x.Age &lt; b</c> hash identically (the value becomes a
    /// parameter), but <c>x.Age &lt; a</c> vs <c>x.Age &gt; a</c> vs <c>x.Name == null</c> all differ.
    ///
    /// Expressions that fold a runtime <i>value</i> into the SQL text (<c>Query.Custom(...)</c>) or use a
    /// node type the WHERE visitor doesn't model are reported as <b>uncacheable</b>, so the caller falls
    /// back to a full translation and never serves wrong SQL from the cache.
    /// </summary>
    internal static class ExpressionStructure
    {
        private const long FnvOffset = unchecked((long)14695981039346656037UL);
        private const long FnvPrime = 1099511628211L;

        // Distinct node tokens.
        private const long TokParam = 0x01;
        private const long TokValue = 0x02;
        private const long TokNullConst = 0x03;
        private const long TokMember = 0x04;
        private const long TokBinary = 0x05;
        private const long TokUnary = 0x06;
        private const long TokMethod = 0x07;
        private const long TokStatic = 0x08;
        private const long TokNullNode = 0x09;

        private readonly struct R
        {
            public readonly long Hash;
            public readonly bool Depends;     // references the row parameter (i.e. a column)
            public readonly bool Cacheable;
            public R(long hash, bool depends, bool cacheable) { Hash = hash; Depends = depends; Cacheable = cacheable; }
        }

        /// <summary>
        /// Produces a structural hash of <paramref name="body"/>. Returns <c>false</c> when the shape must
        /// not be cached (custom raw SQL, or an unmodelled node type).
        /// </summary>
        public static bool TryComputeHash(Expression body, ParameterExpression rowParam, out long hash)
        {
            R r = Compute(body, rowParam);
            hash = r.Hash;
            return r.Cacheable;
        }

        private static R Compute(Expression? e, ParameterExpression rowParam)
        {
            switch (e)
            {
                case null:
                    return new R(TokNullNode, false, true);

                case ParameterExpression p:
                    return new R(TokParam, p == rowParam, true);

                case ConstantExpression c:
                    return new R(c.Value == null ? TokNullConst : TokValue, false, true);

                case MemberExpression m:
                {
                    R inner = Compute(m.Expression, rowParam);
                    if (!inner.Depends)
                        return new R(TokValue, false, inner.Cacheable); // closure/captured value
                    long h = MixString(Mix(FnvOffset, TokMember), m.Member.Name);
                    return new R(Mix(h, inner.Hash), true, inner.Cacheable);
                }

                case UnaryExpression u:
                {
                    R o = Compute(u.Operand, rowParam);
                    if (!o.Depends)
                        return new R(TokValue, false, o.Cacheable);
                    long h = Mix(Mix(FnvOffset, TokUnary), (long)u.NodeType);
                    return new R(Mix(h, o.Hash), true, o.Cacheable);
                }

                case BinaryExpression b:
                {
                    R l = Compute(b.Left, rowParam);
                    R r = Compute(b.Right, rowParam);
                    bool cacheable = l.Cacheable && r.Cacheable;
                    if (!l.Depends && !r.Depends)
                        return new R(TokValue, false, cacheable);
                    long h = Mix(Mix(FnvOffset, TokBinary), (long)b.NodeType);
                    h = Mix(h, l.Hash);
                    h = Mix(h, r.Hash);
                    return new R(h, true, cacheable);
                }

                case MethodCallExpression mc:
                {
                    // Query.Custom(...) / Select.Custom(...) splice a runtime string into the SQL text — never cache.
                    bool cacheable = mc.Method.Name != "Custom";

                    long h = MixString(Mix(FnvOffset, TokMethod), mc.Method.Name);
                    h = Mix(h, mc.Method.DeclaringType?.GetHashCode() ?? 0);

                    R obj = mc.Object != null ? Compute(mc.Object, rowParam) : new R(TokStatic, false, true);
                    bool depends = obj.Depends;
                    cacheable &= obj.Cacheable;
                    h = Mix(h, obj.Hash);

                    var args = mc.Arguments;
                    for (int i = 0; i < args.Count; i++)
                    {
                        R a = Compute(args[i], rowParam);
                        depends |= a.Depends;
                        cacheable &= a.Cacheable;
                        h = Mix(h, a.Hash);
                    }

                    if (!depends)
                        return new R(TokValue, false, cacheable);
                    return new R(h, true, cacheable);
                }

                default:
                    // Unmodelled node type — don't risk a wrong-SQL collision; force a full translation.
                    return new R(0, true, false);
            }
        }

        private static long Mix(long hash, long value)
        {
            hash ^= value;
            return unchecked(hash * FnvPrime);
        }

        private static long MixString(long hash, string? s)
        {
            if (s == null) return Mix(hash, 0);
            for (int i = 0; i < s.Length; i++)
                hash = Mix(hash, s[i]);
            return hash;
        }
    }
#nullable disable
}
