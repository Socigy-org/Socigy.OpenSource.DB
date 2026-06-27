using Socigy.OpenSource.DB.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Socigy.OpenSource.DB.Core
{
#nullable enable
    /// <summary>
    /// Executes two compiled queries combined with a SQL set operator
    /// (UNION, UNION ALL, INTERSECT, INTERSECT ALL, EXCEPT, EXCEPT ALL),
    /// yielding <typeparamref name="T"/> rows.
    /// </summary>
    public class PostgresqlSetQueryCommandBuilder<T> : SqlCommandBuilder<PostgresqlSetQueryCommandBuilder<T>>
        where T : class, IDbTable, new()
    {
        private readonly ICompiledQuery _Lhs;
        private readonly ICompiledQuery _Rhs;
        private readonly string _Operator;

        private int _Limit = -1;
        private int _Offset = -1;

        public PostgresqlSetQueryCommandBuilder(ICompiledQuery lhs, ICompiledQuery rhs, string @operator)
        {
            _Lhs = lhs;
            _Rhs = rhs;
            _Operator = @operator;
        }

        public PostgresqlSetQueryCommandBuilder<T> Limit(int limit) { _Limit = limit; return this; }
        public PostgresqlSetQueryCommandBuilder<T> Offset(int offset) { _Offset = offset; return this; }

        public async IAsyncEnumerable<T> ExecuteAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_Connection == null)
                throw new InvalidOperationException("No connection. Call WithConnection() first.");

            if (_Connection.State != ConnectionState.Open)
                await _Connection.OpenAsync(cancellationToken);

            // using: the command must be disposed when enumeration ends (completion, early break, or exception).
            // Without it every UNION/INTERSECT/EXCEPT execution leaked an NpgsqlCommand — the `await using instr`
            // below disposes only the reader and the diagnostics scope, never the command. (netstandard2.0's
            // DbCommand exposes only synchronous Dispose, like the JoinPlan path; that is sufficient for a command.)
            using var command = _Connection.CreateCommand();
            if (_Transaction != null)
                command.Transaction = _Transaction;

            // Build sub-queries into the same command so parameter names don't collide:
            // Lhs adds @p0, @p1, …; Rhs continues from where Lhs left off (@pN, @p{N+1}, …).
            string lhsSql = _Lhs.Compile(command);
            string rhsSql = _Rhs.Compile(command);

            string sql = $"({lhsSql}) {_Operator} ({rhsSql})";
            if (_Limit >= 0) sql += $" LIMIT {_Limit}";   // Limit(0) must return zero rows, not everything
            if (_Offset >= 0) sql += $" OFFSET {_Offset}";

            command.CommandText = sql;

            await using var instr = await Diagnostics.DbDiagnostics.ExecuteReaderAsync(
                command, "SELECT", ct => command.ExecuteReaderAsync(ct), cancellationToken, _Diagnostics);
            var reader = instr.Reader;
            while (await instr.ReadAsync(cancellationToken))
            {
                var row = new T();
                foreach (var kv in row.GetColumns())
                {
                    try
                    {
                        var ordinal = reader.GetOrdinal(kv.Key);
                        if (!reader.IsDBNull(ordinal))
                            kv.Value.SetValue?.Invoke(reader.GetValue(ordinal));
                    }
                    catch (IndexOutOfRangeException) { }
                }
                yield return row;
            }
        }
    }
#nullable disable
}
