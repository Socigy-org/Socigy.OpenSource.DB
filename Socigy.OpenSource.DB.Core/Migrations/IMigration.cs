using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.Migrations
{
    public interface IMigration
    {
        public string Id { get; }
#nullable enable
        public string? PreviousId { get; }
#nullable disable

        public string UpSql { get; }
        public string DownSql { get; }
    }
}
