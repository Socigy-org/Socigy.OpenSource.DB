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
        ///
        /// <para>
        /// <b>This omits every <c>[Default]</c> column unconditionally</b>, whether or not the row carries a
        /// value for it, unless the column is named in <c>keep</c>. Adding <c>[Default]</c> to an existing
        /// column therefore changes the behaviour of every existing <c>ServerDefaults</c> insert that does not
        /// name it — silently, with no compile error and no log line: the row is written with the database
        /// default instead of the value the application set. Prefer <see cref="ServerDefaultsWhenUnset"/>
        /// unless you specifically want the unconditional behaviour, and see the <c>SCGDB027</c> diagnostic
        /// (set <c>dotnet_diagnostic.SCGDB027.severity = warning</c> in <c>.editorconfig</c>) to audit
        /// existing call sites.
        /// </para>
        /// </summary>
        ServerDefaults = 2,

        /// <summary>
        /// Like <see cref="ServerDefaults"/>, but a <c>[Default]</c> column is omitted <b>only when the
        /// property still holds its CLR type default</b> — <c>0</c>, <c>false</c>, <c>null</c>,
        /// <c>default(DateTime)</c>, <c>Guid.Empty</c>. A value the application actually set is written.
        ///
        /// <para>
        /// This is what callers almost always mean by "let the server fill it in", and it makes <c>keep</c>
        /// unnecessary in the common case. It cannot distinguish "the caller set <c>false</c>" from "the
        /// caller set nothing" on a non-nullable value type — nothing can — but it resolves that ambiguity in
        /// favour of the server default only when the value is genuinely absent-looking, rather than always.
        /// </para>
        /// <para>
        /// <b>Cost on bulk paths.</b> <see cref="ServerDefaults"/> resolves the column set once and reuses one
        /// prepared plan for the whole batch. This mode depends on each row's values, so rows are grouped by
        /// which columns are unset and one plan is prepared per distinct group. A batch whose rows agree (the
        /// usual case) is exactly as fast; a batch that disagrees costs one plan per shape, and the split is
        /// logged at Information with the columns responsible so it is visible rather than mysterious.
        /// </para>
        /// </summary>
        ServerDefaultsWhenUnset = 3,
    }
#nullable disable
}
