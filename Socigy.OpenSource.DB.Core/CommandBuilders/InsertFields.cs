namespace Socigy.OpenSource.DB.Core.CommandBuilders
{
#nullable enable
    /// <summary>
    /// Selects which columns an insert writes from the entity versus which the database fills in.
    /// Passed to the context, static and bulk insert methods so a single value expresses the intent
    /// (instead of two opposite-polarity booleans).
    /// </summary>
    public enum InsertFields
    {
        /// <summary>
        /// The default. Auto-increment columns are omitted (the database generates them); every other
        /// column — <b>including <c>[Default]</c> columns</b> — is written from the entity's current value.
        /// A <c>[Default]</c> property you never set is therefore written as its CLR default (e.g.
        /// <c>default(DateTime)</c>), not via the server default. Use <see cref="ServerDefaults"/> to let the
        /// server fill those.
        /// </summary>
        Default = 0,

        /// <summary>
        /// Also write auto-increment columns from the entity (supply your own identity/sequence values).
        /// Equivalent to the insert builder's <c>WithAllFields()</c>.
        /// </summary>
        IncludeAutoIncrement = 1,

        /// <summary>
        /// Let the database fill both auto-increment and <c>[Default]</c> columns: they are omitted from the
        /// INSERT so their server-side defaults apply. Equivalent to the insert builder's
        /// <c>ExcludeAutoFields()</c>.
        /// </summary>
        ServerDefaults = 2,
    }
#nullable disable
}
