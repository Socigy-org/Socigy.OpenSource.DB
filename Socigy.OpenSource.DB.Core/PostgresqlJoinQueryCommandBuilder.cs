using Socigy.OpenSource.DB.Core.Enums;
using Socigy.OpenSource.DB.Core.Interfaces;
using Socigy.OpenSource.DB.Core.Parsers.Postgresql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Socigy.OpenSource.DB.Core
{
#nullable enable
    /// <summary>
    /// Builds and executes a two-table JOIN query, yielding <c>(T, TJoin)</c> tuples.
    /// </summary>
    public class PostgresqlJoinQueryCommandBuilder<T, TJoin> : SqlCommandBuilder<PostgresqlJoinQueryCommandBuilder<T, TJoin>>
        where T : class, IDbTable, new()
        where TJoin : class, IDbTable, new()
    {
        private readonly JoinType _JoinType;
        private readonly LambdaExpression? _OnExpression;

        private LambdaExpression? _WhereExpression;
        private int _Limit = -1;
        private int _Offset = -1;

        public PostgresqlJoinQueryCommandBuilder(JoinType joinType, LambdaExpression? onExpression)
        {
            _JoinType = joinType;
            _OnExpression = onExpression;
        }

        public PostgresqlJoinQueryCommandBuilder<T, TJoin> Where(Expression<Func<T, TJoin, bool>> where)
        {
            _WhereExpression = where;
            return this;
        }

        public PostgresqlJoinQueryCommandBuilder<T, TJoin> Limit(int limit)
        {
            _Limit = limit;
            return this;
        }

        public PostgresqlJoinQueryCommandBuilder<T, TJoin> Offset(int offset)
        {
            _Offset = offset;
            return this;
        }

        public async IAsyncEnumerable<(T Left, TJoin Right)> ExecuteAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_Connection == null)
                throw new InvalidOperationException("No connection. Call WithConnection() first.");

            if (_Connection.State != ConnectionState.Open)
                await _Connection.OpenAsync(cancellationToken);

            var tInstance = new T();
            var jInstance = new TJoin();

            var tCols = tInstance.GetColumns();
            var jCols = jInstance.GetColumns();

            var selectParts = new List<string>(tCols.Count + jCols.Count);
            var tOverrides = new Dictionary<string, string>(tCols.Count);
            var jOverrides = new Dictionary<string, string>(jCols.Count);

            foreach (var kv in tCols)
            {
                var alias = $"t_{kv.Key}";
                selectParts.Add($"t.{kv.Key} AS {alias}");
                tOverrides[kv.Key] = alias;
            }
            foreach (var kv in jCols)
            {
                var alias = $"j_{kv.Key}";
                selectParts.Add($"j.{kv.Key} AS {alias}");
                jOverrides[kv.Key] = alias;
            }

            var command = _Connection.CreateCommand();
            if (_Transaction != null)
                command.Transaction = _Transaction;

            var sb = new StringBuilder();
            sb.Append("SELECT ");
            sb.Append(string.Join(", ", selectParts));
            sb.Append($" FROM \"{tInstance.GetTableName()}\" AS t");
            sb.Append($" {BuildJoinKeyword(_JoinType)} \"{jInstance.GetTableName()}\" AS j");

            if (_OnExpression != null && (_JoinType & JoinType.Natural) == 0 && _JoinType != JoinType.Cross)
            {
                var tParam = _OnExpression.Parameters[0];
                var jParam = _OnExpression.Parameters[1];
                var visitor = new PostgresqlJoinConditionVisitor(
                    tParam, jParam,
                    name => tInstance.GetDbColumnName(name),
                    name => jInstance.GetDbColumnName(name),
                    command);
                sb.Append($" ON {visitor.Parse(_OnExpression)}");
            }

            if (_WhereExpression != null)
            {
                var tParam = _WhereExpression.Parameters[0];
                var jParam = _WhereExpression.Parameters[1];
                var visitor = new PostgresqlJoinConditionVisitor(
                    tParam, jParam,
                    name => tInstance.GetDbColumnName(name),
                    name => jInstance.GetDbColumnName(name),
                    command);
                sb.Append($" WHERE {visitor.Parse(_WhereExpression)}");
            }

            if (_Limit > 0) sb.Append($" LIMIT {_Limit}");
            if (_Offset > 0) sb.Append($" OFFSET {_Offset}");

            command.CommandText = sb.ToString();

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                yield return (ReadRow<T>(reader, tOverrides), ReadRow<TJoin>(reader, jOverrides));
        }

        private static TRow ReadRow<TRow>(DbDataReader reader, Dictionary<string, string> overrides)
            where TRow : class, IDbTable, new()
        {
            var row = new TRow();
            foreach (var kv in row.GetColumns())
            {
                if (!overrides.TryGetValue(kv.Key, out var alias))
                    continue;
                try
                {
                    var ordinal = reader.GetOrdinal(alias);
                    if (!reader.IsDBNull(ordinal))
                        kv.Value.SetValue?.Invoke(reader.GetValue(ordinal));
                }
                catch (IndexOutOfRangeException) { }
            }
            return row;
        }

        private static string BuildJoinKeyword(JoinType joinType)
        {
            if ((joinType & JoinType.Natural) != 0) return "NATURAL JOIN";
            if (joinType == JoinType.Cross) return "CROSS JOIN";
            if (joinType == JoinType.Inner) return "INNER JOIN";
            if ((joinType & JoinType.Full) == JoinType.Full) return "FULL OUTER JOIN";
            if ((joinType & JoinType.Left) != 0) return "LEFT JOIN";
            if ((joinType & JoinType.Right) != 0) return "RIGHT JOIN";
            return "JOIN";
        }
    }
#nullable disable
}
