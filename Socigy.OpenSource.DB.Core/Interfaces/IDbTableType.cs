using System.Collections.Generic;
using System.Data.Common;
using Socigy.OpenSource.DB.Core.CommandBuilders;

namespace Socigy.OpenSource.DB.Core.Interfaces
{
#nullable enable
    /// <summary>
    /// Implemented (by the source generator) on every <c>[TableType]</c> class. Exposes the type-specific
    /// hooks a generic <c>DynamicTable&lt;T&gt;</c> needs to materialize rows, build INSERTs, stash custom
    /// (undeclared) column values, and emit the runtime <c>CREATE TABLE</c> — all without static-abstract
    /// members (which aren't netstandard2.0-safe). <c>DynamicTable&lt;T&gt;</c> calls these on a throwaway
    /// <c>new T()</c> prototype.
    /// </summary>
    public interface IDbTableType<T> : IDbTable where T : class
    {
        /// <summary>Resolves the column ordinals for the current result set once, for reuse across all rows.</summary>
        int[] ResolveOrdinals(DbDataReader reader, Dictionary<string, string>? columnOverrides = null);

        /// <summary>Materializes one row using pre-resolved ordinals (see <see cref="ResolveOrdinals"/>).</summary>
        T MaterializeRow(DbDataReader reader, int[] ordinals);

        /// <summary>The ordered INSERT column descriptors (table-name independent), optionally including auto-increment columns.</summary>
        InsertColumnDescriptor[] InsertColumns(bool includeAutoIncrement);

        /// <summary>Stores a value for a custom (undeclared) column on this row instance.</summary>
        void SetCustomValue(string name, object? value);

        /// <summary>Reads a previously captured custom (undeclared) column value; returns <see langword="false"/> if absent.</summary>
        bool TryGetCustomValue<TValue>(string name, out TValue? value);

        /// <summary>Builds a <c>CREATE TABLE</c> statement for the declared shape, using the given runtime table name.</summary>
        string GetCreateTableSql(string tableName, bool ifNotExists);
    }
#nullable disable
}
