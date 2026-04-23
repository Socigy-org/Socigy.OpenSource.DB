using System;
using System.CodeDom.Compiler;
using Socigy.OpenSource.DB.Migrations;

/*
    This code was generated using Socigy.OpenSource.DB generation tool at 04/22/2026 13:42:14 by Patrik Stohanzl - 71410855+WailedParsley36@users.noreply.github.com
*/

namespace Example.Auth.DB.Socigy.Migrations
{
    [GeneratedCode("Socigy.OpenSource.DB", "1.0.0+bff7c2df91148854e14d0745a6879ab1190791a7")]
    public class M_202604221342__Changed_Use_23ce5eb7f2 : ILocalMigration
    {
        public string Id => _Id;

        public string UpSql => _UpSql;
        public string DownSql => _DownSql;

        public const string _Id = "202604221342__Changed_Use_23ce5eb7f2";
                public const string _PreviousId = "202604221341__Initial_Migration_90b66f2b06";
        public string PreviousId => _PreviousId;

public const string _UpSql = """
ALTER TABLE "users" ADD COLUMN "parent_role" smallint;
ALTER TABLE "users" ADD CONSTRAINT "FK_ParentRole" FOREIGN KEY ("parent_role") REFERENCES "user_role" ("id");
""";
        
public const string _DownSql = """
ALTER TABLE "users" DROP COLUMN "parent_role";
ALTER TABLE "users" DROP CONSTRAINT IF EXISTS "FK_ParentRole";
""";
    }
}

