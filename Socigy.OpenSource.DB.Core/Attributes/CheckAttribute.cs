using System;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Attaches a CHECK constraint to a table (class-level) or a column (property-level).
    /// Use either the raw-SQL overload or the type-safe <see cref="Socigy.OpenSource.DB.Checks.IDbCheckExpression"/> overload.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
    public class CheckAttribute : Attribute
    {
        /// <summary>Raw SQL expression (used with <c>[Check("sql")]</c>).</summary>
        public string? Statement { get; }

        /// <summary>
        /// A class implementing <see cref="Socigy.OpenSource.DB.Checks.IDbCheckExpression"/>
        /// (used with <c>[Check(typeof(MyCheck))]</c>).
        /// </summary>
        public Type? ExpressionType { get; }

        public string? Name { get; set; }

        /// <summary>Raw SQL string constraint.</summary>
        public CheckAttribute(string statement) => Statement = statement;

        /// <summary>
        /// Type-safe constraint built via <see cref="Socigy.OpenSource.DB.Checks.DbCheck"/>.
        /// <paramref name="expressionType"/> must implement
        /// <see cref="Socigy.OpenSource.DB.Checks.IDbCheckExpression"/> and have a public
        /// parameterless constructor.
        /// </summary>
        public CheckAttribute(Type expressionType) => ExpressionType = expressionType;
    }
}
