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
