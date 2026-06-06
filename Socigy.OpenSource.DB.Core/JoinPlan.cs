using Socigy.OpenSource.DB.Core.Delegates;
using Socigy.OpenSource.DB.Core.Diagnostics;
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
    /// Engine shared by the typed join-builder facades. Accumulates the ordered join steps (driving table +
    /// joins) plus the optional driving predicate, WHERE, ORDER BY, LIMIT/OFFSET, and builds + executes the
    /// SQL. Materializes one entity per joined table per row using <c>aN_</c> column aliases. Non-generic so
    /// every arity reuses one implementation.
    /// </summary>
    public sealed class JoinPlan
    {
        /// <summary>One participating table: a metadata prototype, a factory for fresh rows, its alias, join type, and ON condition (null for the driving table / NATURAL / CROSS).</summary>
        public sealed class JoinStep
        {
            public IDbTable Prototype = null!;
            public Func<IDbTable> Factory = null!;
            public string Alias = "";
            public JoinType Type;
            public LambdaExpression? On;
        }

        public List<JoinStep> Steps { get; } = new();
        public LambdaExpression? DrivingPredicate;   // filters the driving table (alias a0)
        public LambdaExpression? Where;
        public LambdaExpression? OrderBy;
        public bool OrderDesc;
        public int Limit = -1;
        public int Offset = -1;

        public JoinPlan Clone()
        {
            var p = new JoinPlan
            {
                DrivingPredicate = DrivingPredicate,
                Where = Where,
                OrderBy = OrderBy,
                OrderDesc = OrderDesc,
                Limit = Limit,
                Offset = Offset,
            };
            p.Steps.AddRange(Steps);
            return p;
        }

        // Maps the first <paramref name="count"/> lambda parameters positionally to the join steps' aliases.
        private List<(ParameterExpression, string, GetColumnName)> MapFor(LambdaExpression lambda)
        {
            var list = new List<(ParameterExpression, string, GetColumnName)>(lambda.Parameters.Count);
            for (int i = 0; i < lambda.Parameters.Count && i < Steps.Count; i++)
            {
                int idx = i;
                list.Add((lambda.Parameters[i], Steps[idx].Alias, name => Steps[idx].Prototype.GetDbColumnName(name)!));
            }
            return list;
        }

        private static string JoinKeyword(JoinType joinType)
        {
            if ((joinType & JoinType.Natural) != 0) return "NATURAL JOIN";
            if (joinType == JoinType.Cross) return "CROSS JOIN";
            if (joinType == JoinType.Inner) return "INNER JOIN";
            if ((joinType & JoinType.Full) == JoinType.Full) return "FULL OUTER JOIN";
            if ((joinType & JoinType.Left) != 0) return "LEFT JOIN";
            if ((joinType & JoinType.Right) != 0) return "RIGHT JOIN";
            return "JOIN";
        }

        // Builds the full SELECT, binding WHERE/ON parameters to <paramref name="command"/>, and returns the
        // per-step alias→output-alias maps used to read each table back.
        private string BuildSelectSql(DbCommand command, out Dictionary<string, string>[] overrides)
        {
            overrides = new Dictionary<string, string>[Steps.Count];
            var select = new List<string>();
            for (int i = 0; i < Steps.Count; i++)
            {
                var step = Steps[i];
                var map = new Dictionary<string, string>();
                foreach (var kv in step.Prototype.GetColumns())
                {
                    string outAlias = step.Alias + "_" + kv.Key;
                    select.Add(step.Alias + ".\"" + kv.Key + "\" AS " + outAlias);
                    map[kv.Key] = outAlias;
                }
                overrides[i] = map;
            }

            var sb = new StringBuilder("SELECT ");
            sb.Append(string.Join(", ", select));
            AppendFromAndJoins(sb, command);
            AppendWhere(sb, command);
            if (OrderBy != null)
            {
                var visitor = new PostgresqlMultiJoinVisitor(MapFor(OrderBy), command);
                sb.Append(" ORDER BY ").Append(visitor.ResolveColumnList(OrderBy));
                if (OrderDesc) sb.Append(" DESC");
            }
            if (Limit > 0) sb.Append(" LIMIT ").Append(Limit);
            if (Offset > 0) sb.Append(" OFFSET ").Append(Offset);
            return sb.ToString();
        }

        private void AppendFromAndJoins(StringBuilder sb, DbCommand command)
        {
            sb.Append(" FROM \"").Append(Steps[0].Prototype.GetTableName()).Append("\" ").Append(Steps[0].Alias);
            for (int i = 1; i < Steps.Count; i++)
            {
                var step = Steps[i];
                sb.Append(' ').Append(JoinKeyword(step.Type)).Append(" \"")
                  .Append(step.Prototype.GetTableName()).Append("\" ").Append(step.Alias);

                bool noOn = (step.Type & JoinType.Natural) != 0 || step.Type == JoinType.Cross;
                if (step.On != null && !noOn)
                {
                    var visitor = new PostgresqlMultiJoinVisitor(MapFor(step.On), command);
                    sb.Append(" ON ").Append(visitor.Parse(step.On.Body));
                }
            }
        }

        private void AppendWhere(StringBuilder sb, DbCommand command)
        {
            var conditions = new List<string>(2);
            if (DrivingPredicate != null)
            {
                var visitor = new PostgresqlMultiJoinVisitor(MapFor(DrivingPredicate), command);
                conditions.Add(visitor.Parse(DrivingPredicate.Body));
            }
            if (Where != null)
            {
                var visitor = new PostgresqlMultiJoinVisitor(MapFor(Where), command);
                conditions.Add(visitor.Parse(Where.Body));
            }
            if (conditions.Count > 0)
                sb.Append(" WHERE ").Append(string.Join(" AND ", conditions));
        }

        private string BuildScalarSql(DbCommand command, string projection)
        {
            var sb = new StringBuilder("SELECT ").Append(projection);
            AppendFromAndJoins(sb, command);
            AppendWhere(sb, command);
            return sb.ToString();
        }

        public async IAsyncEnumerable<IDbTable?[]> ExecuteRowsAsync(
            DbConnection? connection, DbTransaction? transaction, DbDiagnosticsContext? diagnostics,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (connection == null)
                throw new InvalidOperationException("No connection. Call WithConnection()/WithTransaction() first.");
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var command = connection.CreateCommand();
            if (transaction != null) command.Transaction = transaction;
            command.CommandText = BuildSelectSql(command, out var overrides);

            await using var instr = await DbDiagnostics.ExecuteReaderAsync(
                command, "SELECT", ct => command.ExecuteReaderAsync(ct), cancellationToken, diagnostics).ConfigureAwait(false);
            var reader = instr.Reader;

            // Resolve each table's column ordinals + PK ordinals ONCE per result set (not per row), so the
            // hot loop just reads by ordinal via the generated fast materializer — no per-row dictionary,
            // closures, GetOrdinal-by-name, or boxing.
            int n = Steps.Count;
            var fast = new IOrdinalReadable?[n];
            var ords = new int[n][];
            var pkOrds = new int[n][];
            for (int i = 0; i < n; i++)
            {
                fast[i] = Steps[i].Prototype as IOrdinalReadable;
                if (fast[i] != null) ords[i] = fast[i]!.GetReaderOrdinals(reader, overrides[i]);
                pkOrds[i] = ResolvePkOrdinals(Steps[i].Prototype, reader, overrides[i]);
            }

            while (await instr.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var entities = new IDbTable?[n];
                for (int i = 0; i < n; i++)
                {
                    if (AllDbNull(reader, pkOrds[i])) { entities[i] = null; continue; } // outer-join miss
                    entities[i] = fast[i] != null
                        ? fast[i]!.ReadByOrdinals(reader, ords[i])
                        : MaterializeRowSlow(Steps[i], reader, overrides[i]);
                }
                yield return entities;
            }
        }

        private static int[] ResolvePkOrdinals(IDbTable prototype, DbDataReader reader, Dictionary<string, string> overrides)
        {
            var pk = prototype.GetPrimaryColumns();
            var list = new List<int>(pk.Count);
            foreach (var kv in pk)
            {
                if (!overrides.TryGetValue(kv.Key, out var alias)) continue;
                try { list.Add(reader.GetOrdinal(alias)); } catch (IndexOutOfRangeException) { }
            }
            return list.ToArray();
        }

        // True only when the table has a PK and every PK column is NULL — the canonical outer-join no-match
        // signal. A table without a PK is never treated as a miss.
        private static bool AllDbNull(DbDataReader reader, int[] ordinals)
        {
            if (ordinals.Length == 0) return false;
            foreach (var o in ordinals)
                if (o >= 0 && !reader.IsDBNull(o)) return false;
            return true;
        }

        public async Task<long> CountAsync(
            DbConnection? connection, DbTransaction? transaction, DbDiagnosticsContext? diagnostics, CancellationToken cancellationToken)
        {
            var result = await ScalarAsync(connection, transaction, diagnostics, "COUNT(*)", cancellationToken).ConfigureAwait(false);
            return result == null || result is DBNull ? 0L : Convert.ToInt64(result);
        }

        public Task<object?> AggregateAsync(
            string func, LambdaExpression column,
            DbConnection? connection, DbTransaction? transaction, DbDiagnosticsContext? diagnostics, CancellationToken cancellationToken)
        {
            return ScalarWithColumnAsync(func, column, connection, transaction, diagnostics, cancellationToken);
        }

        private async Task<object?> ScalarWithColumnAsync(
            string func, LambdaExpression column,
            DbConnection? connection, DbTransaction? transaction, DbDiagnosticsContext? diagnostics, CancellationToken cancellationToken)
        {
            if (connection == null)
                throw new InvalidOperationException("No connection. Call WithConnection()/WithTransaction() first.");
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var command = connection.CreateCommand();
            if (transaction != null) command.Transaction = transaction;
            var colVisitor = new PostgresqlMultiJoinVisitor(MapFor(column), command);
            string projection = func + "(" + colVisitor.ResolveSingleColumn(column) + ")";
            command.CommandText = BuildScalarSql(command, projection);
            return await DbDiagnostics.ExecuteScalarAsync(
                command, "SELECT", ct => command.ExecuteScalarAsync(ct), cancellationToken, diagnostics).ConfigureAwait(false);
        }

        private async Task<object?> ScalarAsync(
            DbConnection? connection, DbTransaction? transaction, DbDiagnosticsContext? diagnostics, string projection, CancellationToken cancellationToken)
        {
            if (connection == null)
                throw new InvalidOperationException("No connection. Call WithConnection()/WithTransaction() first.");
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var command = connection.CreateCommand();
            if (transaction != null) command.Transaction = transaction;
            command.CommandText = BuildScalarSql(command, projection);
            return await DbDiagnostics.ExecuteScalarAsync(
                command, "SELECT", ct => command.ExecuteScalarAsync(ct), cancellationToken, diagnostics).ConfigureAwait(false);
        }

        // Fallback for any prototype that doesn't implement IOrdinalReadable (none in practice — all entities
        // are generated). The caller already handled outer-join no-match detection.
        private static IDbTable MaterializeRowSlow(JoinStep step, DbDataReader reader, Dictionary<string, string> overrides)
        {
            var row = step.Factory();
            foreach (var kv in row.GetColumns())
            {
                if (!overrides.TryGetValue(kv.Key, out var alias)) continue;
                try
                {
                    int ordinal = reader.GetOrdinal(alias);
                    if (!reader.IsDBNull(ordinal))
                        kv.Value.SetValue?.Invoke(reader.GetValue(ordinal));
                }
                catch (IndexOutOfRangeException) { }
            }
            return row;
        }
    }
#nullable disable
}
