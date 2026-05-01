using System.Data.Common;

namespace Socigy.OpenSource.DB.Core.Interfaces
{
#nullable enable
    /// <summary>Implemented by query builders that can materialize their SQL and parameters into a provided command.</summary>
    public interface ICompiledQuery
    {
        /// <summary>
        /// Appends all WHERE/SELECT/ORDER-BY parameters to <paramref name="command"/> and
        /// returns the SQL string.  Does not set <see cref="DbCommand.CommandText"/>.
        /// </summary>
        string Compile(DbCommand command);
    }
#nullable disable
}
