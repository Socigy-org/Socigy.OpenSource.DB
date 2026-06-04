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
        public SqlQueryBuilderExpressionParser(DbCommand command, GetColumnName getColumNames, CreateSelectVisitor newSelect, CreateWhereVisitor newWhere, CreateOrderByVisitor newOrderBy)
        {
            _Command = command;
            _GetColumName = getColumNames;
            _Sql = new StringBuilder("SELECT ");

            _NewSelect = newSelect;
            _NewWhere = newWhere;
            _NewOrderBy = newOrderBy;
        }

        public void Process(string tableName, Expression<Func<T, object?[]>>? select, Expression<Func<T, bool>>? where, Expression<Func<T, object?[]>>? orderBy, bool isDescending)
        {
            if (select == null)
                _Sql.Append("* ");
            else
                _Sql.Append(ProcessSelect(select));

            _Sql.Append($" FROM {tableName}");

            if (where != null)
                _Sql.Append(ProcessWhere(where));

            if (orderBy != null)
                _Sql.Append(ProcessOrderBy(orderBy, isDescending));
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
            // Shape cache: translate a given predicate shape once, then on subsequent calls reuse the SQL
            // and only re-bind the parameter values. Only safe when WHERE is the first param-producing
            // clause (so @pN numbering starts at 0 and matches the cached SQL) and the shape is cacheable
            // (no Query.Custom raw SQL / unmodelled nodes).
            if (_Command.Parameters.Count == 0
                && ExpressionStructure.TryComputeHash(where.Body, where.Parameters[0], out long hash))
            {
                var type = typeof(T);
                if (QueryShapeCache.TryGet(type, hash, out string cachedSql, out int expectedParams))
                {
                    _NewWhere(where.Parameters[0], _GetColumName, _Command).BindParameters(where);

                    if (_Command.Parameters.Count == expectedParams)
                        return cachedSql;

                    // Param-count mismatch ⇒ a (vanishingly rare) hash collision against a different shape.
                    // Discard what we bound and fall back to a fresh, correct translation.
                    for (int i = _Command.Parameters.Count - 1; i >= 0; i--)
                        _Command.Parameters.RemoveAt(i);
                    return _NewWhere(where.Parameters[0], _GetColumName, _Command).Parse(where);
                }

                string sql = _NewWhere(where.Parameters[0], _GetColumName, _Command).Parse(where);
                QueryShapeCache.Add(type, hash, sql, _Command.Parameters.Count);
                return sql;
            }

            return _NewWhere(where.Parameters[0], _GetColumName, _Command)
              .Parse(where);
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
