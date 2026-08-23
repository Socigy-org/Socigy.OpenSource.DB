using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Marks a table or column as the continuation of one that used to have a different name, so the migration
    /// generator emits a <c>RENAME</c> that preserves the existing data instead of a DROP + ADD that destroys it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table and a column each have two names — the C# name and the database name — and <b>either is
    /// accepted</b> here, at both levels. Write whichever you are looking at:
    /// </para>
    /// <code>
    /// // These are equivalent.
    /// [Renamed("OwnerId")]  [Column("author_id")] public Guid AuthorId { get; set; }
    /// [Renamed("owner_id")] [Column("author_id")] public Guid AuthorId { get; set; }
    ///
    /// // And at the class level, so are these.
    /// [Renamed("Article")]  [Table("posts")] public partial class Post { }
    /// [Renamed("articles")] [Table("posts")] public partial class Post { }
    /// </code>
    /// <para>
    /// If the old name would match two different tables or columns — one by its C# name and another by its
    /// database name — generation fails rather than guessing, since guessing wrong drops a column's data.
    /// Disambiguate by using the database name, which is unique.
    /// </para>
    /// <para>
    /// The attribute describes one step. Once the migration that performs the rename has been generated, the
    /// old name is recorded in the schema snapshot and the attribute can be removed.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
    public class RenamedAttribute : Attribute
    {
        /// <summary>
        /// The previous name: either the database table/column name, or the C# class/property name.
        /// </summary>
        public string OldName { get; }

        /// <param name="oldName">
        /// The previous name of this table or column. Both the database name and the C# name are accepted.
        /// </param>
        public RenamedAttribute(string oldName)
        {
            OldName = oldName;
        }
    }
}
