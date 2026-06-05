using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;

namespace Socigy.OpenSource.DB.Core.Parsers
{
    public interface ISqlVisitor
    {
        string Parse(Expression expression);
    }
}
