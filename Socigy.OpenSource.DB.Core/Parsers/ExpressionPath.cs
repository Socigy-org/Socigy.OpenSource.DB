using System;
using System.Linq.Expressions;

namespace Socigy.OpenSource.DB.Core.Parsers
{
#nullable enable
    /// <summary>
    /// Locates a sub-expression by a positional path (sequence of child indices) so a parameter slot
    /// recorded against one predicate tree can be re-found in a structurally identical tree on a cache
    /// hit. <see cref="ComputePath"/> and <see cref="Navigate"/> share the same canonical child ordering.
    /// </summary>
    internal static class ExpressionPath
    {
        /// <summary>Returns the index path from <paramref name="root"/> to <paramref name="target"/> (by reference), or null.</summary>
        public static int[]? ComputePath(Expression root, Expression target)
        {
            var buffer = new System.Collections.Generic.List<int>(8);
            return Find(root, target, buffer) ? buffer.ToArray() : null;
        }

        private static bool Find(Expression node, Expression target, System.Collections.Generic.List<int> path)
        {
            if (ReferenceEquals(node, target)) return true;

            int count = ChildCount(node);
            for (int i = 0; i < count; i++)
            {
                Expression? child = GetChild(node, i);
                if (child == null) continue;
                path.Add(i);
                if (Find(child, target, path)) return true;
                path.RemoveAt(path.Count - 1);
            }
            return false;
        }

        /// <summary>Walks <paramref name="path"/> from <paramref name="root"/> to the target node.</summary>
        public static Expression Navigate(Expression root, int[] path)
        {
            Expression node = root;
            for (int i = 0; i < path.Length; i++)
                node = GetChild(node, path[i])!;
            return node;
        }

        private static int ChildCount(Expression node)
        {
            switch (node)
            {
                case BinaryExpression: return 2;
                case UnaryExpression: return 1;
                case MemberExpression m: return m.Expression != null ? 1 : 0;
                case MethodCallExpression mc: return (mc.Object != null ? 1 : 0) + mc.Arguments.Count;
                case LambdaExpression: return 1;
                default: return 0;
            }
        }

        private static Expression? GetChild(Expression node, int index)
        {
            switch (node)
            {
                case BinaryExpression b: return index == 0 ? b.Left : b.Right;
                case UnaryExpression u: return u.Operand;
                case MemberExpression m: return m.Expression;
                case MethodCallExpression mc:
                    if (mc.Object != null)
                        return index == 0 ? mc.Object : mc.Arguments[index - 1];
                    return mc.Arguments[index];
                case LambdaExpression l: return l.Body;
                default: return null;
            }
        }
    }
#nullable disable
}
