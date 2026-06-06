using Socigy.OpenSource.DB.Tool;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;

namespace Socigy.OpenSource.DB.Tool.Tests;

/// <summary>Small builders for hand-crafting <see cref="SchemaDiff"/> inputs to the SQL generator.</summary>
internal static class TestSchema
{
    public static DbColumn Col(string name, string dbType, bool pk = false, bool nullable = false,
        bool autoIncrement = false, string? defaultValue = null, string? dotnetType = null) => new()
    {
        Name = name,
        SourceName = name,
        DatabaseType = dbType,
        DotnetType = dotnetType,
        IsPrimaryKey = pk,
        Nullable = nullable,
        IsAutoIncrement = autoIncrement,
        DefaultValue = defaultValue,
    };

    public static DbTable Table(string name, params DbColumn[] columns) => new()
    {
        Name = name,
        SourceName = name,
        Columns = columns.ToList(),
        Constraints = new List<DbConstraint>(),
    };

    public static DbConstraint ForeignKey(string tableName, string column, string targetTable, string targetColumn,
        string? onDelete = null) => new()
    {
        Type = DbConstraint.Types.ForeignKey,
        TableName = tableName,
        Columns = new[] { column },
        TargetTable = targetTable,
        TargetColumns = new[] { targetColumn },
        OnDelete = onDelete,
    };

    public static DbConstraint Unique(string tableName, params string[] columns) => new()
    {
        Type = DbConstraint.Types.Unique,
        TableName = tableName,
        Columns = columns,
    };

    /// <summary>Sets the ambient <c>Configuration.CurrentSchema</c> the generator reads when resolving FK targets.</summary>
    public static void UseSchema(params DbTable[] tables) =>
        Configuration.CurrentSchema = new DbSchema { Id = "test", Tables = tables.ToList() };
}
