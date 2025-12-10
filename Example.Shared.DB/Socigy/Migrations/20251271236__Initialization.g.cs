using System;
using System.CodeDom.Compiler;
using Socigy.OpenSource.DB.Migrations;

/*
    This code was generated using Socigy.OpenSource.DB generation tool at 12/07/2025 12:36:01 by Patrik Stohanzl - stohapat@fit.cvut.cz
*/

namespace Example.Shared.DB.Socigy.Migrations
{
    [GeneratedCode("Socigy.OpenSource.DB", "1.0.0")]
    public class M_20251271236__Initialization : IMigration
    {
        public string Id => _Id;

        public string UpSql => _UpSql;
        public string DownSql => _DownSql;

        public const string _Id = "20251271236__Initialization";
        #nullable enable
        public string? PreviousId => null;
        #nullable disable

public const string _UpSql = """
CREATE TABLE ""shared"" (
    ""id"" uuid,
    CONSTRAINT ""PK_shared"" PRIMARY KEY (""id"")
);
""";
        
public const string _DownSql = """
DROP TABLE IF EXISTS ""shared"" CASCADE;
""";
    }
}

