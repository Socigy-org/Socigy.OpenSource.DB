using System;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Marks a class as a <em>table type</em>: a typed column shape whose actual table name is bound at
    /// runtime via <c>WithTableName(...)</c> / <c>MapTypeAsync(...)</c> (see <c>DynamicTable&lt;T&gt;</c>),
    /// instead of being fixed at compile time like <see cref="TableAttribute"/>.
    /// <para>
    /// Use it for tables created at runtime (per-tenant, per-period, sharded) whose names aren't known at
    /// build time. A <c>[TableType]</c> class is not part of the migration history; it can create/drop its
    /// own table at runtime via <c>InstantiateAsync</c>/<c>DeleteInstanceAsync</c>. A class may carry both
    /// <c>[Table]</c> (a default fixed name) and <c>[TableType]</c> (runtime override).
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class TableTypeAttribute : Attribute
    {
    }
}
