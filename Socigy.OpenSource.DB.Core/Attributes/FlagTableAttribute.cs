using System;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Marks a class as an explicit junction (flag/N:M) table.
    /// Use alongside <see cref="FlaggedEnumTableAttribute"/> on the owning table's property
    /// when the junction table requires extra columns beyond its two FK columns.
    /// The class is processed by the migration tool and its columns are included in schema generation.
    /// </summary>
    /// <example><code>
    /// [FlagTable("users_user_role")]
    /// partial class UsersUserRole
    /// {
    ///     [PrimaryKey, ForeignKey(typeof(User))]
    ///     public Guid UserId { get; set; }
    ///
    ///     [PrimaryKey, ForeignKey(typeof(UserRole))]
    ///     public int UserRoleId { get; set; }
    ///
    ///     [Default(DbDefaults.Time.Now)]
    ///     public DateTime AssignedAt { get; set; }
    /// }
    /// </code></example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class FlagTableAttribute : Attribute
    {
        /// <summary>The SQL table name for this junction table.</summary>
        public string TableName { get; }

        /// <summary>Declares this class as a junction table with the given SQL name.</summary>
        public FlagTableAttribute(string tableName) { TableName = tableName; }
    }
}
