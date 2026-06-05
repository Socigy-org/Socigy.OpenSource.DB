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
        InsertPlan GetInsertPlan();
    }
#nullable disable
}
