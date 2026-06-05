using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Socigy.OpenSource.DB.Core.Parsers
{
#nullable enable
    /// <summary>One bound parameter recorded during translation: which sub-expression produced it and how.</summary>
    internal readonly struct RecordedParameter
    {
        public readonly Expression Source;
        public readonly ParamTransform Transform;
        public readonly Type? ArrayElementType;

        public RecordedParameter(Expression source, ParamTransform transform, Type? arrayElementType)
        {
            Source = source;
            Transform = transform;
            ArrayElementType = arrayElementType;
        }
    }

    /// <summary>
    /// Implemented by a WHERE visitor that records, in binding order, the source expression + transform
    /// for each parameter it emits — so the <see cref="QueryShapeCache"/> can rebuild a path-based replay
    /// plan and bind parameters on a cache hit without re-running the visitor.
    /// </summary>
    internal interface IParameterRecorder
    {
        IReadOnlyList<RecordedParameter> RecordedParameters { get; }
    }
#nullable disable
}
