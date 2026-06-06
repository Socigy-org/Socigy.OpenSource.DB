using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

#nullable enable
namespace Socigy.OpenSource.DB.Core.SyntaxHelper
{
    public static class DB
    {
        /// <summary>
        /// Translation marker for referencing a column that isn't declared on the entity — used with
        /// <c>DynamicTable&lt;T&gt;</c> to filter/sort on a runtime ("custom") column, e.g.
        /// <c>x =&gt; DB.CustomField&lt;int&gt;("score") &gt; 10</c>. It never executes (returns <c>default</c>);
        /// the query translator rewrites it to the quoted column name.
        /// </summary>
        public static TValue CustomField<TValue>(string columnName)
        {
            return default!;
        }

        public static class OrderBy
        {
            public static T Desc<T>(T value)
            {
                return value;
            }
            public static T Asc<T>(T value)
            {
                return value;
            }

            public static string Custom(string customSql)
            {
                return customSql;
            }
        }

        public class Select
        {
            public static object? Custom(string customSql)
            {
                return null!;
            }
            public static object? All()
            {
                return null!;
            }

            #region Case
            public static Select Case()
            {
                return new Select();
            }
            public Select End()
            {
                return this;
            }
            public Select When(bool condition)
            {
                return this;
            }

            /// <summary>
            /// Specifies the value to be used in the THEN clause of a conditional expression.
            /// </summary>
            /// <typeparam name="T">The type of the value to be used in the THEN clause.</typeparam>
            /// <param name="value">The value to assign to the THEN clause. Cannot be an array or implement <see cref="IEnumerable"/>.</param>
            /// <returns>The current <see cref="Select"/> instance for method chaining.</returns>
            /// <exception cref="InvalidDataException">Thrown if <paramref name="value"/> is an array or implements <see cref="IEnumerable"/>.</exception>
            public Select Then<T>(T? value)
            {
                if (typeof(T).IsArray || value is Array || value is IEnumerable)
                    throw new InvalidDataException("Array types are not supported in THEN clause!");

                return this;
            }

            /// <summary>
            /// Specifies the value to use in the ELSE clause of the selection statement when no previous conditions are
            /// met.
            /// </summary>
            /// <typeparam name="T">The type of the value to be used in the ELSE clause.</typeparam>
            /// <param name="value">The value to use in the ELSE clause. Cannot be an array or implement <see cref="IEnumerable"/>.</param>
            /// <returns>The current <see cref="Select"/> instance with the ELSE clause applied.</returns>
            /// <exception cref="InvalidDataException">Thrown if <paramref name="value"/> is an array or implements <see cref="IEnumerable"/>, as array types
            /// are not supported in the ELSE clause.</exception>
            public Select Else<T>(T? value)
            {
                if (typeof(T).IsArray || value is Array || value is IEnumerable)
                    throw new InvalidDataException("Array types are not supported in ELSE clause!");

                return this;
            }

            public Select As(string colName)
            {
                return this;
            }
            #endregion
        }

        public class Query
        {
            public static bool Custom(string customSql)
            {
                return false;
            }
        }
    }
}
#nullable disable