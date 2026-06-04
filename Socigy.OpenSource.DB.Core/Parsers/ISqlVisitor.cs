using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;

namespace Socigy.OpenSource.DB.Core.Parsers
{
    public interface ISqlVisitor
    {
        string Parse(Expression expression);

        /// <summary>
        /// Re-runs the same traversal as <see cref="Parse"/> but emits no SQL — it only appends the
        /// parameters to the command, in the same order. Used by the <see cref="QueryShapeCache"/> on a
        /// cache hit, where the SQL is already known and only the values need binding.
        /// </summary>
        void BindParameters(Expression expression);
    }
}
