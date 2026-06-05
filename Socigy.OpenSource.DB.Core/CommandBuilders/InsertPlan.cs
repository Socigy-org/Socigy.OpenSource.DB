using System;

namespace Socigy.OpenSource.DB.Core.CommandBuilders
{
#nullable enable
    /// <summary>
    /// One column in a cached <see cref="InsertPlan"/>: the parameter name, its CLR type (for DB-type
    /// inference), whether it's a JSON column, and a closure that reads the (already JSON-serialized /
    /// converter-applied) value off a row instance. Built once per entity and reused for every insert.
    /// </summary>
    public sealed class InsertColumnDescriptor
    {
        public string ParameterName { get; }
        public Type Type { get; }
        public bool IsJson { get; }
        public Func<object, object?> GetValue { get; }

        public InsertColumnDescriptor(string parameterName, Type type, bool isJson, Func<object, object?> getValue)
        {
            ParameterName = parameterName;
            Type = type;
            IsJson = isJson;
            GetValue = getValue;
        }
    }

    /// <summary>
    /// A precomputed plan for the default INSERT of an entity: the static SQL text plus the ordered
    /// column descriptors. Lets the insert builder skip rebuilding the SQL and allocating the column
    /// dictionary (and its per-column callbacks) on every call.
    /// </summary>
    public sealed class InsertPlan
    {
        public string CommandText { get; }
        public InsertColumnDescriptor[] Columns { get; }

        public InsertPlan(string commandText, InsertColumnDescriptor[] columns)
        {
            CommandText = commandText;
            Columns = columns;
        }
    }
#nullable disable
}
