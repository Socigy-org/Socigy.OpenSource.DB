using Socigy.OpenSource.DB.Core.CommandBuilders;

namespace Socigy.OpenSource.DB.Core.Interfaces
{
#nullable enable
    /// <summary>
    /// Implemented by generated entities to expose a cached <see cref="InsertPlan"/> for the default
    /// INSERT (all columns except auto-increment). Lets the insert builder skip rebuilding the SQL and
    /// the per-call column dictionary on the common path. Optional — builders fall back to the dictionary
    /// path when an entity doesn't implement it.
    /// </summary>
    public interface IInsertPlanProvider
    {
        /// <summary>The default insert plan: all columns except auto-increment (the database generates those).</summary>
        InsertPlan GetInsertPlan();

        /// <summary>
        /// The insert plan, optionally including auto-increment columns. Pass <see langword="true"/> to insert
        /// every column (e.g. to supply your own identity/sequence values) — the equivalent of the insert
        /// builder's <c>WithAllFields()</c>.
        /// </summary>
        InsertPlan GetInsertPlan(bool includeAutoIncrement);
    }
#nullable disable
}
