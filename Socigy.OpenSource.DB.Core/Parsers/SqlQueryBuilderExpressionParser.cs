using Socigy.OpenSource.DB.Core.Delegates;
using Socigy.OpenSource.DB.Core.Interfaces;
using Socigy.OpenSource.DB.Core.Parsers.Delegates;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text;

namespace Socigy.OpenSource.DB.Core.Parsers
{
    public class SqlQueryBuilderExpressionParser<T>
       where T : IDbTable
    {
        private readonly StringBuilder _Sql;
        private readonly DbCommand _Command;
        private readonly GetColumnName _GetColumName;

        private readonly CreateSelectVisitor _NewSelect;
        private readonly CreateWhereVisitor _NewWhere;
        private readonly CreateOrderByVisitor _NewOrderBy;

        // Per-column [ValueConvertor] lookup: property name -> ConvertToDbValue wrapper, or null for a
        // non-convertor column. Null for tables with no convertor columns (the common case), so the WHERE
        // visitor's convertor branch is skipped entirely and there is zero translation overhead.
        private readonly Func<string, Func<object?, object?>?>? _GetColumnConvertor;

        public SqlQueryBuilderExpressionParser(DbCommand command, GetColumnName getColumNames, CreateSelectVisitor newSelect, CreateWhereVisitor newWhere, CreateOrderByVisitor newOrderBy,
            Func<string, Func<object?, object?>?>? getColumnConvertor = null)
        {
            _Command = command;
            _GetColumName = getColumNames;
            _Sql = new StringBuilder("SELECT ");

            _NewSelect = newSelect;
            _NewWhere = newWhere;
            _NewOrderBy = newOrderBy;
            _GetColumnConvertor = getColumnConvertor;
        }

        /// <summary>
        /// Single entry point used by the generated query builder. For the common <c>Query(predicate)</c>
        /// shape (no projection/order-by/limit/offset) it uses the <see cref="QueryShapeCache"/>: the SQL
        /// is translated once per predicate shape, and subsequent calls skip the visitor entirely — they
        /// replay a parameter plan (navigate to each source sub-expression, evaluate, transform, bind) and
        /// reuse the cached SQL string. Everything else falls back to a full translation.
        /// </summary>
        public string BuildCommand(string tableName, Expression<Func<T, object?[]>>? select, Expression<Func<T, bool>>? where,
            Expression<Func<T, object?[]>>? orderBy, bool isDescending, int limit, int offset,
            string[]? selectColumns = null, string[]? orderByColumns = null)
        {
            // limit < 0 is the "no limit set" sentinel (-1). limit == 0 is an explicit Limit(0) and must emit
            // LIMIT 0 (zero rows), so it cannot take the no-limit fast path below. The string-named projection /
            // order-by (the AOT-safe Select/OrderBy overloads) also disable the fast path, like their Expression forms.
            if (select == null && orderBy == null && selectColumns == null && orderByColumns == null
                && where != null && limit < 0 && offset <= 0
                && _Command.Parameters.Count == 0
                && ExpressionStructure.TryComputeHash(where.Body, where.Parameters[0], out long hash))
            {
                var type = typeof(T);

                if (QueryShapeCache.TryGet(type, hash, out CompiledQuery cached))
                {
                    ReplayPlan(where.Body, cached.Plan);
                    return cached.Sql;
                }

                var visitor = _NewWhere(where.Parameters[0], _GetColumName, _Command);
                if (_GetColumnConvertor != null && visitor is Postgresql.PostgresqlWhereVisitor pwv)
                    pwv.ColumnConvertor = _GetColumnConvertor;
                _Sql.Clear();
                _Sql.Append("SELECT * FROM ");
                _Sql.Append(tableName);
                _Sql.Append(visitor.Parse(where.Body));
                string fullSql = _Sql.ToString();

                // A predicate that applied a [ValueConvertor] is not cacheable: the replay path rebinds from the
                // source expression and would skip the convertor, binding the raw value against the converted column.
                if (visitor is IParameterRecorder recorder && !recorder.UsedConvertor
                    && TryBuildPlan(where.Body, recorder.RecordedParameters, out ParamSlot[] plan))
                {
                    QueryShapeCache.Add(type, hash, new CompiledQuery(fullSql, plan));
                }

                return fullSql;
            }

            // Non-cacheable shape — full translation.
            Process(tableName, select, where, orderBy, isDescending, selectColumns, orderByColumns);
            // >= 0 (not > 0): Limit(0) must emit LIMIT 0 to return zero rows. -1 (unset) emits nothing.
            if (limit >= 0) AddLimit(limit);
            if (offset > 0) AddOffset(offset);
            return ToString();
        }

        // Re-binds parameters for a cached query by navigating to each recorded source sub-expression.
        private void ReplayPlan(Expression body, ParamSlot[] plan)
        {
            for (int i = 0; i < plan.Length; i++)
            {
                ParamSlot slot = plan[i];
                Expression node = ExpressionPath.Navigate(body, slot.Path);
                object? value = WhereParameter.Apply(slot.Transform, ExpressionEvaluator.Evaluate(node), slot.ArrayElementType);

                var p = _Command.CreateParameter();
                p.ParameterName = $"@p{_Command.Parameters.Count}";
                p.Value = value ?? DBNull.Value;
                _Command.Parameters.Add(p);
            }
        }

        // Converts recorded parameter sources (binding order) into positional paths for replay.
        private static bool TryBuildPlan(Expression body, IReadOnlyList<RecordedParameter> recorded, out ParamSlot[] plan)
        {
            plan = new ParamSlot[recorded.Count];
            for (int i = 0; i < recorded.Count; i++)
            {
                RecordedParameter r = recorded[i];
                int[] path = ExpressionPath.ComputePath(body, r.Source);
                if (path == null) { plan = null; return false; }
                plan[i] = new ParamSlot(path, r.Transform, r.ArrayElementType);
            }
            return true;
        }

        public void Process(string tableName, Expression<Func<T, object?[]>>? select, Expression<Func<T, bool>>? where, Expression<Func<T, object?[]>>? orderBy, bool isDescending,
            string[]? selectColumns = null, string[]? orderByColumns = null)
        {
            // A named list with at least one non-empty entry projects those columns; an all-empty/whitespace array
            // (e.g. Select("")) is treated as "no projection" and falls back to * rather than emitting "SELECT  FROM".
            if (HasNonEmpty(selectColumns))
                AppendNamedColumnList(selectColumns!, descending: false);
            else if (select == null)
                _Sql.Append("* ");
            else
                _Sql.Append(ProcessSelect(select));

            _Sql.Append($" FROM {tableName}");

            if (where != null)
                _Sql.Append(ProcessWhere(where));

            if (HasNonEmpty(orderByColumns))
            {
                _Sql.Append(" ORDER BY ");
                AppendNamedColumnList(orderByColumns!, isDescending);
            }
            else if (orderBy != null)
                _Sql.Append(ProcessOrderBy(orderBy, isDescending));
        }

        // True when the array has at least one non-empty name (so it produces a non-empty column list).
        private static bool HasNonEmpty(string[]? columns)
        {
            if (columns == null) return false;
            foreach (var c in columns)
                if (!string.IsNullOrEmpty(c)) return true;
            return false;
        }

        // Builds a quoted column list from string names (the AOT-safe Select/OrderBy overloads). A name is resolved
        // to its DB column via the same map the visitors use; one that does not resolve is taken to be a DB name
        // already. ORDER BY descending applies DESC per column (matching the OrderBy visitor's output).
        private void AppendNamedColumnList(string[] columns, bool descending)
        {
            bool first = true;
            foreach (var name in columns)
            {
                if (string.IsNullOrEmpty(name))
                    continue;
                if (!first)
                    _Sql.Append(", ");
                first = false;
                string? db = _GetColumName(name);
                string ident = !string.IsNullOrEmpty(db) ? db! : name;
                // Double any embedded quote so an unresolved (raw) name cannot break out of the quoted identifier.
                _Sql.Append('"').Append(ident.Replace("\"", "\"\"")).Append('"');
                if (descending)
                    _Sql.Append(" DESC");
            }
        }

        public void AddLimit(int limit)
        {
            _Sql.Append($" LIMIT {limit} ");
        }
        public void AddOffset(int offset)
        {
            _Sql.Append($" OFFSET {offset} ");
        }

        public string ProcessSelect(Expression<Func<T, object?[]>> select)
        {
            return _NewSelect(select.Parameters[0], _GetColumName, _Command)
                .Parse(select);
        }

        public string ProcessWhere(Expression<Func<T, bool>> where)
        {
            var visitor = _NewWhere(where.Parameters[0], _GetColumName, _Command);
            if (_GetColumnConvertor != null && visitor is Postgresql.PostgresqlWhereVisitor pwv)
                pwv.ColumnConvertor = _GetColumnConvertor;
            return visitor.Parse(where);
        }

        public string ProcessOrderBy(Expression<Func<T, object?[]>> orderBy, bool isDesc)
        {
            return _NewOrderBy(orderBy.Parameters[0], _GetColumName, _Command, isDesc)
               .Parse(orderBy);
        }

        public override string ToString()
        {
            return _Sql.ToString();
        }
    }

}
