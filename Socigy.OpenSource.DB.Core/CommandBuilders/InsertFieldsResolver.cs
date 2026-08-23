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
            HashSet<string>? kept = keep == null ? null : ExtractDbColumnNames(keep, sample);
            return ApplyServerDefaults(columns, fields, keep != null, kept);
        }

        /// <summary>
        /// AOT-safe overload of <see cref="Resolve{T}(InsertColumnDescriptor[], InsertFields, Expression{Func{T, object[]}}, IDbTable)"/>
        /// that names the kept columns by string instead of an <c>Expression</c> selector (which forces
        /// <c>Expression.NewArrayInit</c> at the call site — <c>[RequiresDynamicCode]</c>, unusable under NativeAOT).
        /// Each name is a property name (e.g. <c>nameof(Row.Id)</c>); a value that is already a DB column name is
        /// accepted as-is.
        /// </summary>
        public static InsertColumnDescriptor[] Resolve(
            InsertColumnDescriptor[] columns,
            InsertFields fields,
            string[]? keepColumns,
            IDbTable sample)
        {
            // A non-null keepColumns implies ServerDefaults (matching the Expression overload, where a non-null
            // selector — even an empty array — drops the unlisted [Default] columns so the server fills them).
            bool hasKeep = keepColumns != null;
            HashSet<string>? kept = hasKeep ? MapDbColumnNames(keepColumns!, sample) : null;
            return ApplyServerDefaults(columns, fields, hasKeep, kept);
        }

        // Shared filter: when the server fills [Default] columns (ServerDefaults, or any keep), drop those columns so
        // the database default applies — except the ones named in <paramref name="kept"/> (matched by DB column name).
        private static InsertColumnDescriptor[] ApplyServerDefaults(
            InsertColumnDescriptor[] columns, InsertFields fields, bool hasKeep, HashSet<string>? kept)
        {
            bool serverDefaults = fields == InsertFields.ServerDefaults
                || fields == InsertFields.ServerDefaultsWhenUnset
                || hasKeep;
            if (!serverDefaults)
                return columns;

            var result = new List<InsertColumnDescriptor>(columns.Length);
            foreach (var d in columns)
            {
                if (d.HasDbDefault && (kept == null || !kept.Contains(d.ParameterName.Substring(1))))
                    continue;
                result.Add(d);
            }
            return result.ToArray();
        }

        /// <summary>
        /// Whether the plan for <paramref name="fields"/> depends on each row's values rather than on the
        /// entity's shape alone. True only for <see cref="InsertFields.ServerDefaultsWhenUnset"/>: every other
        /// mode resolves its column set once and reuses one plan for a whole batch, which is what makes the
        /// bulk paths fast. Callers use this to decide whether the per-row grouping below is needed at all.
        /// </summary>
        public static bool IsRowDependent(InsertFields fields)
            => fields == InsertFields.ServerDefaultsWhenUnset;

        /// <summary>
        /// The <see cref="InsertFields.ServerDefaultsWhenUnset"/> filter for one row: a <c>[Default]</c> column
        /// is omitted only when <paramref name="row"/> still holds its CLR type default for it, so a value the
        /// caller actually set survives.
        /// </summary>
        /// <param name="kept">Columns named in <c>keep</c>, which are always written regardless of value.</param>
        public static InsertColumnDescriptor[] ResolveForRow(
            InsertColumnDescriptor[] columns, object row, HashSet<string>? kept)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));

            var result = new List<InsertColumnDescriptor>(columns.Length);
            foreach (var d in columns)
            {
                if (d.HasDbDefault
                    && (kept == null || !kept.Contains(d.ParameterName.Substring(1)))
                    && IsClrDefault(d.Type, d.GetValue(row)))
                    continue;
                result.Add(d);
            }
            return result.ToArray();
        }

        /// <summary>
        /// A stable key describing which columns a row omits, so rows that agree can share one prepared plan.
        /// Rows in a batch usually agree, in which case the batch costs exactly one plan as before.
        /// </summary>
        public static string RowShapeKey(InsertColumnDescriptor[] resolved)
        {
            var names = new string[resolved.Length];
            for (int i = 0; i < resolved.Length; i++)
                names[i] = resolved[i].ParameterName;
            return string.Join(",", names);
        }

        /// <summary>
        /// Whether <paramref name="value"/> is the CLR default for <paramref name="type"/> — <c>null</c>,
        /// <c>0</c>, <c>false</c>, <c>default(DateTime)</c>, <c>Guid.Empty</c>, and so on.
        ///
        /// This is the whole judgement <see cref="InsertFields.ServerDefaultsWhenUnset"/> rests on, and it
        /// deliberately cannot tell "the caller set false" from "the caller set nothing" on a non-nullable
        /// value type — no runtime check can. What it does is resolve that ambiguity towards the server
        /// default only when the value looks genuinely absent, instead of always.
        /// </summary>
        public static bool IsClrDefault(Type type, object? value)
        {
            if (value == null || value is DBNull) return true;

            // A nullable column can express "unset" exactly — as null, handled above. So a non-null value in
            // one was chosen by the caller, including an explicit 0 or false, and is written. Only a
            // non-nullable value type has the "present but indistinguishable from unset" problem this whole
            // mode exists to make a judgement call about.
            if (Nullable.GetUnderlyingType(type) != null) return false;

            // A reference type holding a non-null value was set by definition.
            if (!type.IsValueType) return false;

            // Boxed comparison against a freshly zeroed instance of the same type. Enums compare correctly
            // (their zero member equals default), as do DateTime, Guid, TimeSpan and the numeric primitives.
            var zero = type.IsEnum ? Enum.ToObject(type, 0) : GetValueTypeDefault(type);
            return zero != null && zero.Equals(value);
        }

        // Zero-initializes a struct without Activator.CreateInstance(Type), whose
        // [DynamicallyAccessedMembers(PublicParameterlessConstructor)] requirement would produce an IL2xxx
        // trim warning here. A struct needs no preserved constructor — the runtime just zeroes it — but the
        // trimmer cannot see that, so the common types are listed and the rest fall back safely.
        private static object? GetValueTypeDefault(Type type)
        {
            if (type == typeof(int)) return default(int);
            if (type == typeof(long)) return default(long);
            if (type == typeof(bool)) return default(bool);
            if (type == typeof(Guid)) return default(Guid);
            if (type == typeof(DateTime)) return default(DateTime);
            if (type == typeof(DateTimeOffset)) return default(DateTimeOffset);
            if (type == typeof(decimal)) return default(decimal);
            if (type == typeof(double)) return default(double);
            if (type == typeof(float)) return default(float);
            if (type == typeof(short)) return default(short);
            if (type == typeof(byte)) return default(byte);
            if (type == typeof(sbyte)) return default(sbyte);
            if (type == typeof(ushort)) return default(ushort);
            if (type == typeof(uint)) return default(uint);
            if (type == typeof(ulong)) return default(ulong);
            if (type == typeof(char)) return default(char);
            if (type == typeof(TimeSpan)) return default(TimeSpan);

            // An exotic struct: treat it as set rather than guess. Omitting a column the caller may have
            // populated is the damaging direction, so the safe answer here is "not default".
            return null;
        }

        /// <summary>
        /// The DB column names a <c>keep</c> selector refers to. Exposed so the bulk paths can reuse the
        /// expression-to-column mapping for the per-row filter without duplicating the visitor plumbing.
        /// </summary>
        public static HashSet<string> ExtractMemberNames<T>(Expression<Func<T, object?[]>> keep, IDbTable sample)
            => ExtractDbColumnNames(keep, sample);

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

        // Maps each supplied name to its DB column name (the same mapping the expression path applies after extracting
        // member names). A name that resolves via GetDbColumnName is a property name; one that does not is taken to be
        // a DB column name already (so a generated "&lt;Prop&gt;ColumnName" constant also works).
        public static HashSet<string> MapDbColumnNames(string[] names, IDbTable sample)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name))
                    continue;
                var db = sample.GetDbColumnName(name);
                set.Add(!string.IsNullOrEmpty(db) ? db! : name);
            }
            return set;
        }
    }
#nullable disable
}
