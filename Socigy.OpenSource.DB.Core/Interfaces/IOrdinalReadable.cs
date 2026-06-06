using System.Collections.Generic;
using System.Data.Common;

namespace Socigy.OpenSource.DB.Core.Interfaces
{
#nullable enable
    /// <summary>
    /// Exposes the generated fast materializer (ordinals resolved once per result set, values read with the
    /// allocation-free <c>GetFieldValue&lt;T&gt;</c>, no per-row dictionary or boxing) to non-generic callers
    /// such as the join engine. Implemented by every generated entity.
    /// </summary>
    public interface IOrdinalReadable
    {
        /// <summary>Resolves this table's column ordinals for the current result set (optionally via output-alias overrides).</summary>
        int[] GetReaderOrdinals(DbDataReader reader, Dictionary<string, string>? overrides);

        /// <summary>Materializes one row using pre-resolved ordinals.</summary>
        IDbTable ReadByOrdinals(DbDataReader reader, int[] ordinals);
    }
#nullable disable
}
