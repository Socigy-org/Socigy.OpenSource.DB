using Socigy.OpenSource.DB.Core.CommandBuilders;
using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Core.Interfaces
{
    public interface IDbTable
    {
        string GetTableName();
        Dictionary<string, ColumnInfo> GetColumns();
        Dictionary<string, ColumnInfo> GetPrimaryColumns();
        (string Name, ColumnInfo Info)? GetColumn(string name);
        /// <summary>Maps a C# member name to its database column name.</summary>
        string? GetDbColumnName(string memberName);
    }
}
