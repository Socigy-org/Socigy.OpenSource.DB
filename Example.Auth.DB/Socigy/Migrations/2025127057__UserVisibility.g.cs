using System;
using System.CodeDom.Compiler;
using Socigy.OpenSource.DB.Migrations;

/*
    This code was generated using Socigy.OpenSource.DB generation tool at 12/07/2025 00:57:16 by Patrik Stohanzl - stohapat@fit.cvut.cz
*/

namespace Example.Auth.DB.Socigy.Migrations
{
    [GeneratedCode("Socigy.OpenSource.DB", "1.0.0")]
    public class M_2025127057__UserVisibility : IMigration
    {
        public string Id => _Id;

        public string UpSql => _UpSql;
        public string DownSql => _DownSql;

        public const string _Id = "2025127057__UserVisibility";
                public const string _PreviousId = "2025127045__Initialization";
        public string PreviousId => _PreviousId;

public const string _UpSql = """
UPDATE ""user_visibility"" SET ""value"" = 'CirclesOnli' WHERE ""id"" = 1;
""";
        
public const string _DownSql = """
UPDATE ""user_visibility"" SET ""value"" = 'CirclesOnly' WHERE ""id"" = 1;
""";
    }
}

