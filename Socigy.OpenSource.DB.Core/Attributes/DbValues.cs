using System;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Cross-platform sentinel constants for common attribute values.
    /// Each constant is translated to the appropriate SQL keyword by the target DB engine.
    /// </summary>
    public static class DbValues
    {
        internal const string Prefix = "$socigy$val$";

        /// <summary>
        /// Referential action constants for <see cref="ForeignKeyAttribute.OnDelete"/>
        /// and <see cref="ForeignKeyAttribute.OnUpdate"/>.
        /// </summary>
        public static class ForeignKey
        {
            /// <summary>Delete/update the child rows when the parent row is deleted/updated.</summary>
            public const string Cascade = "$socigy$val$fk.cascade";

            /// <summary>Set the foreign key column(s) to NULL when the parent row is deleted/updated.</summary>
            public const string SetNull = "$socigy$val$fk.set_null";

            /// <summary>Set the foreign key column(s) to their default value when the parent row is deleted/updated.</summary>
            public const string SetDefault = "$socigy$val$fk.set_default";

            /// <summary>Prevent deletion/update of the parent row if child rows exist (default DB behaviour).</summary>
            public const string Restrict = "$socigy$val$fk.restrict";

            /// <summary>Same as <see cref="Restrict"/> but the check is deferred until end of transaction.</summary>
            public const string NoAction = "$socigy$val$fk.no_action";
        }
    }
}
