using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Core.Enums
{
    [Flags]
    public enum JoinType
    {
        // 0 is usually reserved for None/Default
        None = 0,

        Inner = 1 << 0,
        Cross = 1 << 1,

        Left = 1 << 2,
        Right = 1 << 3,

        // Composite: Full is semantically both Left AND Right
        Full = Left | Right,

        // "NATURAL" changes how columns are matched
        Natural = 1 << 4,

        // "OUTER" is often optional syntax (LEFT JOIN vs LEFT OUTER JOIN),
        // but this flag allows you to be explicit if your SQL dialect requires it.
        ExplicitOuter = 1 << 5
    }
}
