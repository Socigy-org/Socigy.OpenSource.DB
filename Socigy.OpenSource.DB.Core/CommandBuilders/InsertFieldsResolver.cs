using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Socigy.OpenSource.DB.Core.Interfaces;
using Socigy.OpenSource.DB.Core.Parsers.Postgresql;

namespace Socigy.OpenSource.DB.Core.CommandBuilders
{
#nullable enable
    /// <summary>
    /// Shared logic that applies an <see cref="InsertFields"/> option (and an optional per-column "keep"
    /// selector) to a precomputed set of insert column descriptors. Used by the generated context/static
    /// insert methods, <c>BulkCopy</c>, and <c>DynamicTable</c> so every path interprets the option identically.
    /// </summary>
    public static class InsertFieldsResolver
    {
        /// <summary>Whether the insert plan should include auto-increment columns for the given option.</summary>
        public static bool IncludesAutoIncrement(InsertFields fields)
            => fields == InsertFields.IncludeAutoIncrement;

        /// <summary>
        /// Filters <paramref name="columns"/> for the chosen <paramref name="fields"/>. When the option lets the
        /// server fill <c>[Default]</c> columns (<see cref="InsertFields.ServerDefaults"/>, or any time
        /// <paramref name="keep"/> is supplied), those columns are dropped so the database default applies —
        /// except the columns named by <paramref name="keep"/>, whose values you supply yourself.
        /// </summary>
        /// <param name="columns">The plan columns, already fetched with or without auto-increment per <see cref="IncludesAutoIncrement"/>.</param>
        /// <param name="fields">The field-control option.</param>
        /// <param name="keep">Optional selector naming the <c>[Default]</c> columns to write yourself; the server fills the rest.</param>
        /// <param name="sample">Any row instance, used to map the selector's members to DB column names.</param>
        public static InsertColumnDescriptor[] Resolve<T>(
            InsertColumnDescriptor[] columns,
            InsertFields fields,
            Expression<Func<T, object?[]>>? keep,
            IDbTable sample)
        {
            bool serverDefaults = fields == InsertFields.ServerDefaults || keep != null;
            if (!serverDefaults)
                return columns;

            HashSet<string>? kept = keep == null ? null : ExtractDbColumnNames(keep, sample);

            var result = new List<InsertColumnDescriptor>(columns.Length);
            foreach (var d in columns)
            {
                if (d.HasDbDefault && (kept == null || !kept.Contains(d.ParameterName.Substring(1))))
                    continue;
                result.Add(d);
            }
            return result.ToArray();
        }

        private static HashSet<string> ExtractDbColumnNames<T>(Expression<Func<T, object?[]>> keep, IDbTable sample)
        {
            var visitor = new PostgresqlUpdateVisitor(keep.Parameters[0], sample.GetDbColumnName!, null!);
            var members = visitor.ExtractColumnNames(keep);
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in members)
            {
                var db = sample.GetDbColumnName(name);
                if (!string.IsNullOrEmpty(db))
                    set.Add(db!);
            }
            return set;
        }
    }
#nullable disable
}
