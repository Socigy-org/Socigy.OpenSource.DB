using System;

namespace Socigy.OpenSource.DB.Core.Parsers
{
    /// <summary>
    /// Describes a flagged-enum junction table relationship for use by SQL expression visitors.
    /// An instance is passed to the WHERE visitor so that <c>x.Role.HasFlag(value)</c> expressions
    /// can be translated to <c>EXISTS (SELECT 1 FROM junction WHERE fk = main.pk AND enum_fk = @v)</c>.
    /// </summary>
    public sealed class FlaggedEnumJoinInfo
    {
        /// <summary>The junction table name (e.g. <c>users_user_role</c>).</summary>
        public string JunctionTable { get; set; } = "";

        /// <summary>The owning/main table name (e.g. <c>users</c>).</summary>
        public string MainTable { get; set; } = "";

        /// <summary>
        /// Pairs of (main-table PK column, junction-table FK column) that link the two tables.
        /// E.g. <c>[("id", "user_id")]</c>.
        /// </summary>
        public (string MainPkCol, string JunctionFkCol)[] PkMappings { get; set; } = new (string, string)[0];

        /// <summary>The junction-table column that holds the enum/flag value (e.g. <c>user_role_id</c>).</summary>
        public string EnumFkColumn { get; set; } = "";
    }
}
