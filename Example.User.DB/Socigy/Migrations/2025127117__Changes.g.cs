using System;
using System.CodeDom.Compiler;
using Socigy.OpenSource.DB.Migrations;

/*
    This code was generated using Socigy.OpenSource.DB generation tool at 12/07/2025 01:17:11 by Patrik Stohanzl - stohapat@fit.cvut.cz
*/

namespace Example.User.DB.Socigy.Migrations
{
    [GeneratedCode("Socigy.OpenSource.DB", "1.0.0")]
    public class M_2025127117__Changes : IMigration
    {
        public string Id => _Id;

        public string UpSql => _UpSql;
        public string DownSql => _DownSql;

        public const string _Id = "2025127117__Changes";
        #nullable enable
        public string? PreviousId => null;
        #nullable disable

public const string _UpSql = """
CREATE TABLE ""user_login"" (
    ""id"" uuid,
    ""email"" text,
    ""username"" text,
    ""first_name"" text,
    ""last_name"" text,
    CONSTRAINT ""UQ_Email"" UNIQUE (""email""),
    CONSTRAINT ""PK_user_login"" PRIMARY KEY (""id"")
);
""";
        
public const string _DownSql = """
DROP TABLE IF EXISTS ""user_login"" CASCADE;
""";
    }
}

