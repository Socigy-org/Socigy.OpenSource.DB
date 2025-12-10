using Example.Shared.DB;
using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Core.Interfaces;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using static Example.Auth.DB.DB;

namespace Example.Auth.DB
{
    [Table("user_visibility")]
    public enum UserVisibility : short
    {
        [Description("This will make the user visible to everyone")]
        Public,
        CirclesOnly,
        CustomCircles
    }

    [Table("users")]
    [Check("LEN(email) < 25")]
    public partial class User
    {
        [PrimaryKey]
        public Guid ID { get; set; }

        public string Username { get; set; }
        public short Tag { get; set; }

        public string? IconUrl { get; set; }

        [StringLength(10)] // TOOD: Implement this
        public string Email { get; set; }
        public bool EmailVerified { get; set; }
        public bool RegistrationComplete { get; set; }

        public string? PhoneNumber { get; set; }

        public string FirstName { get; set; }
        public string LastName { get; set; }

        public DateTime? BirthDate { get; set; }

        public bool IsChild { get; set; }
        public Guid? ParentId { get; set; }

        public UserVisibility Visibility { get; set; }

        //public static async IAsyncEnumerable<User> Query(Expression<Func<User, bool>> condition)
        //{
        //    DbTransaction transaction = null!;
        //    DbConnection connection = null!;
        //    DbBatch batch = null!;

        //    // It's what will be in the DB but accessible as a constant in code rather than using a nameof(User.ID) which would be bad. This will be for all columns
        //    // public const string IdColumnName = "id";
        //    User.IdColumnName;

        //    var users = await User.QueryAsync(x => x.ParentId != null && x.BirthDate < DateTime.UtcNow && x.Email == "example@example.com");
        //    users = await User.Transaction(transaction).QueryAsync(x => x.ParentId != null && x.BirthDate < DateTime.UtcNow && x.Email == "example@example.com");
        //    users = await User.Connection(connection).QueryAsync(x => x.ParentId != null && x.BirthDate < DateTime.UtcNow && x.Email == "example@example.com");

        //    var newUser = await new User()
        //    {
        //        Email = "invalid"
        //    }.InsertAsync();

        //    newUser = await new User()
        //    {
        //        Email = "invalid2"
        //    }.Batch(batch).InsertAsync();


        //    newUser.Email = "new";
        //    newUser.EmailVerified = true;
        //    // Also an option without batch or with .Transaction(), .Connection()
        //    await newUser.Batch(batch).UpdateAsync(x => new { x.EmailVerified, x.Email });

        //    // Also an option without batch or with .Transaction(), .Connection()
        //    await newUser.Batch(batch).DeleteAsync();

        //    // ↓ There should be options (where it makes sense) for batch or for .Transaction() and .Connection() ↓

        //    // Query(Expression<Func<(UserCourse UserCourse, Course Course, User User), bool>> condition)
        //    UserCourse.JoinUser().JoinCourse().QueryAsync(x => x.User.Email == "Aasdasd" && x.UserCourse.RegisteredAt < DateTime.UtcNow);

        //    var oneUser = await User.FirstOrDefaultAsync(x => x.ParentId != null && x.BirthDate < DateTime.UtcNow && x.Email == "example@example.com");
        //    oneUser = await User.FirstAsync(x => x.ParentId != null && x.BirthDate < DateTime.UtcNow && x.Email == "example@example.com");
        //    bool tmp = await User.AnyAsync(x => x.ParentId == null);
        //    int tmp = await User.CountAsync();

        //    // This can be done to other tables that doesnt have FK constraint as well
        //    await UserCourse.Join<User>((userCourse, user) => userCourse.UserId == user.ID).Query(/* the resulting SQL should be without WHERE clause */);
        //    await UserCourse.Join<User>((userCourse, user) => userCourse.UserId == user.ID).Query(x => x.User.EmailVerified == true);

        //    users = await User.OrderBy(x => x.BirthDate).Offset(10).Limit(100).QueryAsync(x => x.ParentId != null && x.BirthDate < DateTime.UtcNow && x.Email == "example@example.com");
        //    users = await User.OrderBy(x => x.BirthDate).Top(100).QueryAsync(x => x.ParentId != null && x.BirthDate < DateTime.UtcNow && x.Email == "example@example.com");

        //    // Raw commands
        //    await AuthDb.Query("SELECT * FROM ...");
        //    await AuthDb.Scalar("SELECT 1 FROM ...");
        //    await AuthDb.Execute("INSERT INTO user(...) VALUES(...)");

        //    throw new NotImplementedException();
        //}

        public async Task TestAsync()
        {
            string username = "wailed";

            var users = User.Query(x => x.ParentId == Guid.NewGuid() || x.IsChild && x.Visibility == UserVisibility.Public)
                // SELECT id,email,username ....
                .Select(x => new object?[] { x.ID, x.Email, x.Username })

                // SELECT id, emailVerified, username ...
                .Select(x => new object?[] { x.ID, username == "wailed" ? x.Email : x.EmailVerified, x.Username })

                // SELECT email AS "emailer", username = 'this_value' ... 
                .Select(x => new object?[] { Select.Custom($"{x.Email} AS \"emailer\", {x.Username} = 'this_value'") })

                // SELECT id, CASE WHEN email = @p0 OR username LIKE ('%Example%') OR username LIKE ('Example%') OR username LIKE ('%Example') THEN true ELSE false END AS "is_example", username ...
                // p0 = 'example@example.com'
                // p1 = 'Example'
                .Select(x => new object?[] {
                    x.ID,
                    Select.Case()
                        .When(x.Email == "exampl@example.com" ||
                              x.Username.Contains("Example") /* LIKE ("%Example%") */ ||
                              x.Username.StartsWith("Example") /* LIKE ("Example%") */ ||
                              x.Username.EndsWith("Example") /* LIKE ("%Example") */)
                        .Then(true)
                        .Else(false)
                        .As("is_example"),
                    x.Username
                })
                .OrderBy(x => new object?[] { x.Email, OrderBy.Desc(x.BirthDate) })
                .ExecuteAsync();

            await foreach (var user in users)
            {
                var isExample = user.GetCustomValue<bool>("is_example");
                if (!isExample)
                    Console.WriteLine(user.Email);
            }
        }
    }

    public partial class User
    {
        public static UserTableQueryBuilder Query(Expression<Func<User, bool>> where)
        {
            return new UserTableQueryBuilder().Where(where);
        }

        public static UserTableQueryBuilder Query()
        {
            return new UserTableQueryBuilder();
        }

        public static User ConvertFrom(DbDataReader reader, Dictionary<string, string>? columnOverrides = null)
        {

            return new User();
        }
    }

    public static class DB
    {
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
        }

        public class Select
        {
            public static object? Custom(string customSql)
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
            public Select Then(object? value)
            {
                return this;
            }
            public Select As(string colName)
            {
                return this;
            }
            public Select Else(object? value)
            {
                return this;
            }
            #endregion
        }
    }

    public struct UserTableQueryBuilder
    {
        private DbConnection? _Connection;
        private DbTransaction? _Transaction;
        private DbBatch? _Batch;

        private Expression<Func<User, object?[]>> _SelectClause;
        private Expression<Func<User, bool>> _WhereClause;

        private Expression<Func<User, object?[]>>? _OrderByClause;
        private bool _OrderByDescending = false;

        private int _Limit = -1;
        private int _Offset = -1;

        public UserTableQueryBuilder() { }

        /// <summary>
        /// Associates the specified database transaction with the query builder instance. Use this or <see cref="WithConnection(DbConnection)"/> to specify the context for query execution.
        /// </summary>
        /// <remarks>Use this method to ensure that all queries executed by this builder participate in
        /// the provided transaction. This is useful for maintaining atomicity across multiple operations.</remarks>
        /// <exception cref="ArgumentNullException">Thrown if the provided <paramref name="transaction"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown if the provided <paramref name="transaction"/> has no associated DbConnection.</exception>
        /// <param name="transaction">The database transaction to use for subsequent query operations. Cannot be null.</param>
        /// <returns>The current <see cref="UserTableQueryBuilder"/> instance with the transaction applied.</returns>
        public UserTableQueryBuilder WithTransaction(DbTransaction transaction)
        {
            _Transaction = transaction;
            if (_Transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            else if (_Transaction.Connection == null)
                throw new ArgumentException("The provided transaction has no associated DbConnection.", nameof(transaction));

            _Connection = _Transaction.Connection;

            return this;
        }

        /// <summary>
        /// Sets the database connection to be used for subsequent queries and returns the current builder instance. Use this or <see cref="WithTransaction(DbTransaction)"/> to specify the context for query execution."/>
        /// </summary>
        /// <remarks>This method enables fluent configuration of the query builder. The provided
        /// connection will be used for all future query operations performed by this instance.</remarks>
        /// <exception cref="ArgumentNullException">Thrown if the provided <paramref name="connection"/> is null.</exception>
        /// <param name="connection">The database connection to associate with the query builder. Cannot be null.</param>
        /// <returns>The current <see cref="UserTableQueryBuilder"/> instance with the specified connection set.</returns>
        public UserTableQueryBuilder WithConnection(DbConnection connection)
        {
            _Connection = connection;
            if (_Connection == null)
                throw new ArgumentNullException(nameof(connection));

            return this;
        }

        /// <summary>
        /// Associates the specified database batch operation with the query builder, enabling batched execution of user
        /// table queries.
        /// </summary>
        /// <remarks>Use this method to execute multiple queries as part of a single batch operation. This
        /// can improve performance and ensure atomicity when supported by the underlying database.</remarks>
        /// <exception cref="ArgumentNullException">Thrown if the provided <paramref name="batch"/> is null and Connection/Transaction was not set.</exception>
        /// <param name="batch">The database batch to use for executing queries. If null the connection/transaction must be specified!</param>
        /// <returns>The current <see cref="UserTableQueryBuilder"/> instance with the batch operation applied.</returns>
        public UserTableQueryBuilder WithBatch(DbBatch? batch)
        {
            if (batch == null)
            {
                if (_Connection == null && _Transaction == null)
                    throw new ArgumentNullException(nameof(batch), "If batch is null, either connection or transaction must be specified!");

                _Batch = _Connection?.CreateBatch() ?? _Transaction!.Connection?.CreateBatch() ?? throw new InvalidOperationException("The provided transaction has no DbConnection from which a DbBatch could be created");
                _Batch.Transaction = _Transaction;
            }
            else
                _Batch = batch;

            return this;
        }

        /// <summary>
        /// Specifies the columns to select when querying the user table.
        /// </summary>
        /// <remarks>If not called, all columns will be selected by default. This method allows for
        /// projection of only the desired columns, which can improve query performance and reduce data
        /// transfer.</remarks>
        /// <param name="select">An expression that defines which properties of the <see cref="User"/> entity to include in the result set.
        /// The expression should return an array of selected property values.</param>
        /// <returns>The current <see cref="UserTableQueryBuilder"/> instance, enabling method chaining.</returns>
        public UserTableQueryBuilder Select(Expression<Func<User, object?[]>> select)
        {
            _SelectClause = select;
            return this;
        }

        /// <summary>
        /// Adds a filter condition to the query that determines which users are included based on the specified
        /// predicate.
        /// </summary>
        /// <remarks>Only one filter condition can be set per query builder instance; subsequent calls to
        /// <c>Where</c> will overwrite the previous condition.</remarks>
        /// <param name="where">An expression that defines the criteria used to select users. The predicate should return <see
        /// langword="true"/> for users to include in the results.</param>
        /// <returns>The current <see cref="UserTableQueryBuilder"/> instance, enabling method chaining for additional query
        /// configuration.</returns>
        public UserTableQueryBuilder Where(Expression<Func<User, bool>> where)
        {
            _WhereClause = where;
            return this;
        }

        /// <summary>
        /// Sets the maximum number of user records to return in the query results.
        /// </summary>
        /// <param name="limit">The maximum number of user records to include in the result set. Must be zero or a positive integer.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the provided <paramref name="limit"/> is negative.</exception>
        /// <returns>The current <see cref="UserTableQueryBuilder"/> instance with the limit applied.</returns>
        public UserTableQueryBuilder Limit(int limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(limit);

            _Limit = limit;
            return this;
        }

        /// <summary>
        /// Sets the number of rows to skip before starting to return results from the query.
        /// </summary>
        /// <remarks>Use this method to implement paging in query results. Calling this method overwrites
        /// any previously set offset value.</remarks>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the provided <paramref name="offset"/> is negative.</exception>
        /// <param name="offset">The number of rows to skip. Must be zero or greater.</param>
        /// <returns>The current <see cref="UserTableQueryBuilder"/> instance with the offset applied.</returns>
        public UserTableQueryBuilder Offset(int offset)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);

            _Offset = offset;
            return this;
        }

        /// <summary>
        /// Specifies the ordering of results for the query using the provided expression.
        /// </summary>
        /// <remarks>If multiple properties are specified in the array, results will be ordered by each
        /// property in the order they appear. This method can be chained with other query builder methods to construct
        /// complex queries.</remarks>
        /// <param name="clause">An expression that defines one or more properties of the <see cref="User"/> entity to order the results by.
        /// The array elements determine the order of precedence. You can use <see cref="DB.OrderBy"/> for complex ordering (default will be ASC)</param>
        /// <returns>The current <see cref="UserTableQueryBuilder"/> instance with the ordering clause applied.</returns>
        public UserTableQueryBuilder OrderBy(Expression<Func<User, object?[]>> clause)
        {
            _OrderByClause = clause;
            return this;
        }

        /// <summary>
        /// Specifies a descending order for the query results based on the provided user properties.
        /// </summary>
        /// <remarks>Call this method to order the query results by one or more user properties in
        /// descending order. This method can be chained with other query builder methods to construct complex
        /// queries.</remarks>
        /// <param name="clause">An expression that selects one or more properties of the <see cref="User"/> entity to use for ordering the
        /// results in descending order. You can use <see cref="DB.OrderBy"/> for complex ordering (default will be DESC)</param>
        /// <returns>The current <see cref="UserTableQueryBuilder"/> instance to allow method chaining.</returns>
        public UserTableQueryBuilder OrderByDesc(Expression<Func<User, object?[]>> clause)
        {
            _OrderByClause = clause;
            _OrderByDescending = true;
            return this;
        }

        /// <summary>
        /// Adds a new command to the current batch operation.
        /// </summary>
        /// <remarks>This method should be called only after a batch has been initialized using
        /// WithBatch(). Attempting to add to a batch without initialization will result in an exception.</remarks>
        /// <exception cref="InvalidOperationException">Thrown if no batch has been provided. Call WithBatch() before invoking this method.</exception>
        public void AddToBatch()
        {
            if (_Batch == null)
                throw new InvalidOperationException("Cannot add to batch when no DbBatch was provided. Please call WithBatch() first.");

            var batchCommand = _Batch.CreateBatchCommand();
            _Batch.BatchCommands.Add(batchCommand);
        }

        /// <summary>
        /// Adds a new command to the current database batch asynchronously.
        /// </summary>
        /// <remarks>This method should be called only after a batch has been initialized using
        /// WithBatch(). It is typically used to accumulate multiple commands for execution as a single batch
        /// operation.</remarks>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown if no database batch has been provided. Call WithBatch() before invoking this method.</exception>
        public async Task AddToBatchAsync()
        {
            if (_Batch == null)
                throw new InvalidOperationException("Cannot add to batch when no DbBatch was provided. Please call WithBatch() first.");

            var batchCommand = _Batch.CreateBatchCommand();
            _Batch.BatchCommands.Add(batchCommand);
        }

        public async IAsyncEnumerable<User> ExecuteAsync()
        {
            if (_Batch != null)
                throw new InvalidOperationException("Cannot execute command, when DbBatch was provided. Please call AddToBatchAsync()");
            else if (_Connection == null)
                throw new InvalidOperationException("Cannot execute command, when no DbConnection was provided. Please call WithConnection()/WithTransaction() first");

            var command = _Connection.CreateCommand();

            // TODO: IMPLEMENTATION
            var parser = new SqlQueryBuilderExpressionParser<User>(command, User.GetColumnDbName);
            parser.Process(User.TableName, _SelectClause, _WhereClause, _OrderByClause, _OrderByDescending);
            if (_Limit > 0)
                parser.AddLimit(_Limit);
            if (_Offset > 0)
                parser.AddOffset(_Offset);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                yield return User.ConvertFrom(reader);
            }
        }
    }

    public delegate string? GetColumnName(string sourceName);

    public class SqlQueryBuilderExpressionParser<T>
        where T : IDbTable
    {
        private readonly StringBuilder _Sql;
        private readonly DbCommand _Command;
        private readonly GetColumnName _GetColumName;
        public SqlQueryBuilderExpressionParser(DbCommand command, GetColumnName getColumNames)
        {
            _Command = command;
            _Sql = new StringBuilder("SELECT ");
        }

        public void Process(string tableName, Expression<Func<T, object?[]>>? select, Expression<Func<T, bool>>? where, Expression<Func<T, object?[]>>? orderBy, bool isDescending)
        {
            if (select == null)
                _Sql.Append("* ");
            else
                _Sql.Append(ProcessSelect(select));

            _Sql.Append($"FROM {tableName} ");

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

            string selectSql = new SelectExpressionParser<User>(User.GetColumnDbName)
                .Parse(select).GetSql();

            return selectSql;
        }

        public string ProcessWhere(Expression<Func<T, bool>> where)
        {
            var clause = new StringBuilder();

            // TODO:

            return clause.ToString();
        }

        public string ProcessOrderBy(Expression<Func<T, object?[]>> orderBy, bool isDesc)
        {
            var clause = new StringBuilder();

            // TODO:

            return clause.ToString();
        }

        public override string ToString()
        {
            return _Sql.ToString();
        }
    }

    public class SelectExpressionParser<T> : ExpressionVisitor
        where T : IDbTable
    {
        private static bool IsParameterAccess(Expression node)
        {
            if (node is MemberExpression mem && mem.Expression != null)
                return IsParameterAccess(mem.Expression);
            if (node is ParameterExpression)
                return true;
            if (node is UnaryExpression unary)
                return IsParameterAccess(unary.Operand);

            return false;
        }

        private readonly StringBuilder _sql = new();
        public string GetSql() => _sql.ToString();

        private readonly GetColumnName _GetColumnName;
        public SelectExpressionParser(GetColumnName getColumnName)
        {
            _GetColumnName = getColumnName;
        }

        public string Parse(Expression<Func<T, object?[]>> select)
        {
            var clause = new StringBuilder();

            // x => new object?[] { x.Email, x.Id, x.Username }
            // x => new object?[] { x.Email, false ? x.ParentId : x.Id, x.Username }
            // SELECT "email", "id", "username" FROM users

            // SELECT "email", CASE WHEN "is_child" THEN "parent_id" ELSE "id" END AS "Id_or_ParentId", "username" FROM users
            if (select.Body is NewArrayExpression newArray)
            {
                foreach (var expr in newArray.Expressions)
                {
                    if (IsParameterAccess(expr))
                    {
                        _GetColumnName("");
                    }
                }
            }
            // x => x.Email
            else if (IsParameterAccess(select))
            {
            }

            return clause.ToString();
        }
    }


    public class SqlExpressionParser(DbCommand command) : ExpressionVisitor
    {
        private readonly StringBuilder _sql = new();
        public readonly List<DbParameter> _parameters = [];
        private readonly DbCommand _command = command;
        private readonly string _parameterPrefix = "@p"; // Use @p for generic, Npgsql handles translation to $1 internally usually
        private int _paramIndex = 0;

        public string GetSql() => _sql.ToString();

        // -------------------------------------------------------------------------
        // WHERE CLAUSE LOGIC
        // -------------------------------------------------------------------------

        public void ParseWhere(Expression expression)
        {
            Visit(expression);
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            _sql.Append("(");
            Visit(node.Left);

            switch (node.NodeType)
            {
                case ExpressionType.AndAlso:
                    _sql.Append(" AND ");
                    break;
                case ExpressionType.OrElse:
                    _sql.Append(" OR ");
                    break;
                case ExpressionType.Equal:
                    _sql.Append(IsNullConstant(node.Right) ? " IS " : " = ");
                    break;
                case ExpressionType.NotEqual:
                    _sql.Append(IsNullConstant(node.Right) ? " IS NOT " : " <> ");
                    break;
                case ExpressionType.GreaterThan:
                    _sql.Append(" > ");
                    break;
                case ExpressionType.LessThan:
                    _sql.Append(" < ");
                    break;
                case ExpressionType.GreaterThanOrEqual:
                    _sql.Append(" >= ");
                    break;
                case ExpressionType.LessThanOrEqual:
                    _sql.Append(" <= ");
                    break;
                default:
                    throw new NotSupportedException($"Operator {node.NodeType} is not supported");
            }

            Visit(node.Right);
            _sql.Append(")");
            return node;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            // If the expression refers to the parameter (e.g., x.Email), it's a Column
            if (IsParameterAccess(node))
            {
                // Simple mapping: Property Name = Column Name. 
                // In a real app, you might apply SnakeCase logic here (e.g. BirthDate -> birth_date)
                _sql.Append($"\"{node.Member.Name}\"");
                return node;
            }

            // If it's a boolean property used standalone (e.g., x.IsChild), treat as x.IsChild = TRUE
            if (node.Type == typeof(bool) && IsParameterAccess(node))
            {
                _sql.Append($"\"{node.Member.Name}\" = TRUE");
                return node;
            }

            // Otherwise, it's a captured variable or constant (e.g. UserVisibility.Public)
            AddParameter(EvaluateExpression(node));
            return node;
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            AddParameter(node.Value);
            return node;
        }

        // -------------------------------------------------------------------------
        // ORDER BY LOGIC
        // -------------------------------------------------------------------------

        public void ParseOrderBy(Expression<Func<User, object?[]>> orderBy, bool defaultDesc)
        {
            // The body is usually a NewArrayInitExpression: new object[] { x.A, x.B }
            if (orderBy.Body is NewArrayExpression newArray)
            {
                var parts = new List<string>();
                foreach (var expr in newArray.Expressions)
                {
                    parts.Add(ParseOrderElement(expr, defaultDesc));
                }
                _sql.Append(string.Join(", ", parts));
            }
            else
            {
                // Handle single item: OrderBy(x => new object[] { x.Prop }) 
                // or edge cases where array creation is optimized away
                _sql.Append(ParseOrderElement(orderBy.Body, defaultDesc));
            }
        }

        private string ParseOrderElement(Expression expr, bool defaultGlobalDesc)
        {
            // Remove "Convert" (boxing to object) wrapper if present
            if (expr.NodeType == ExpressionType.Convert)
            {
                expr = ((UnaryExpression)expr).Operand;
            }

            // Check for Method Call (OrderBy.Desc / OrderBy.Asc)
            if (expr is MethodCallExpression methodCall && methodCall.Method.DeclaringType == typeof(OrderBy))
            {
                bool isDesc = methodCall.Method.Name == "Desc";
                var propExpr = methodCall.Arguments[0]; // The 'x.BirthDate' inside Desc()
                return $"{GetColumnName(propExpr)} {(isDesc ? "DESC" : "ASC")}";
            }

            // Standard Property Access
            return $"{GetColumnName(expr)} {(defaultGlobalDesc ? "DESC" : "ASC")}";
        }

        private string GetColumnName(Expression expr)
        {
            if (expr is MemberExpression member)
                return $"\"{member.Member.Name}\"";

            // Handle Unary (Convert) again just in case
            if (expr is UnaryExpression unary && unary.Operand is MemberExpression mem)
                return $"\"{mem.Member.Name}\"";

            throw new NotSupportedException($"Cannot order by expression type: {expr.GetType().Name}");
        }

        // -------------------------------------------------------------------------
        // HELPERS
        // -------------------------------------------------------------------------

        private void AddParameter(object? value)
        {
            var p = _command.CreateParameter();
            p.ParameterName = $"{_parameterPrefix}{_paramIndex++}";
            p.Value = value ?? DBNull.Value;
            _parameters.Add(p);
            _command.Parameters.Add(p);
            _sql.Append(p.ParameterName);
        }

        private bool IsParameterAccess(Expression node)
        {
            // Recursively check if the root of this expression is the parameter 'x'
            if (node is MemberExpression mem)
                return IsParameterAccess(mem.Expression);
            if (node is ParameterExpression)
                return true;
            if (node is UnaryExpression unary)
                return IsParameterAccess(unary.Operand);

            return false;
        }

        private object? EvaluateExpression(Expression node)
        {
            // This compiles the sub-expression (like Guid.NewGuid() or local variables) 
            // and runs it to get the actual value.
            var lambda = Expression.Lambda(node);
            var compiled = lambda.Compile();
            return compiled.DynamicInvoke();
        }

        private bool IsNullConstant(Expression exp)
        {
            return exp is ConstantExpression c && c.Value == null;
        }

        internal void ParseSelect(Expression<Func<User, object?[]>> selectClause)
        {
            throw new NotImplementedException();
        }
    }




    [Table("courses")]
    public partial class Course
    {
        [PrimaryKey]
        public Guid Id { get; set; }

        public string Name { get; set; } = "DEFAULT NAME";

        [Default("timezone('utc', now())")]
        public DateTime CreatedAt { get; set; }
    }


    [Table("user_course")]
    public partial class UserCourse
    {
        [PrimaryKey, ForeignKey(typeof(User))]
        public Guid UserId { get; set; }
        [PrimaryKey, ForeignKey(typeof(Course))]
        public Guid CourseId { get; set; } // Test of type matching

        public DateTime RegisteredAt { get; set; }
    }

    [Table("user_course_agreement")]
    [ForeignKey(typeof(UserCourse), Keys = [nameof(UserId), nameof(CourseId)], TargetKeys = [nameof(UserCourse.UserId), nameof(UserCourse.CourseId)])]
    public partial class UserCourseAgreement
    {
        public Guid UserId { get; set; }
        public Guid CourseId { get; set; }
    }

    //public interface IDbValueConvertor<T>
    //{
    //    public T? ConvertFromDbValue(object? value);
    //    public object? ConvertToDbValue(T? value);
    //}

    //public class NumberToEnumConverter<T> : IDbValueConvertor<T>
    //    where T : Enum
    //{
    //    T? IDbValueConvertor<T>.ConvertFromDbValue(object? value)
    //    {
    //        if (value == DBNull.Value)
    //            return default;

    //        return default;
    //    }

    //    object? IDbValueConvertor<T>.ConvertToDbValue(T? value)
    //    {
    //        throw new NotImplementedException();
    //    }
    //}
}
