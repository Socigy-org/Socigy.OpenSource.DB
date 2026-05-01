using System;
using System.CodeDom.Compiler;
using Socigy.OpenSource.DB.Migrations;

/*
    This code was generated using Socigy.OpenSource.DB generation tool at 05/01/2026 15:18:42 by Patrik Stohanzl - 71410855+WailedParsley36@users.noreply.github.com
*/

namespace Example.Auth.DB.Socigy.Migrations
{
    [GeneratedCode("Socigy.OpenSource.DB", "1.0.0+20fe462d9d10499761d2b271cda16d05e6013716")]
    public class M_202605011518_Added_procedures_73149000fa : ILocalMigration
    {
        public string Id => _Id;

        public string UpSql => _UpSql;
        public string DownSql => _DownSql;

        public const string _Id = "202605011518_Added_procedures_73149000fa";
                public const string _PreviousId = "202604221752__ssdfghgj_3d1c5b8534";
        public string PreviousId => _PreviousId;

public const string _UpSql = """
DROP TABLE IF EXISTS "user_test" CASCADE;
ALTER TABLE "user_login" ALTER COLUMN "username" SET DEFAULT '';
""";
        
public const string _DownSql = """
ALTER TABLE "user_login" ALTER COLUMN "username" SET DEFAULT 'Tvoje máma';
CREATE TABLE "user_test" (
    "user_id" uuid,
    "course_id" uuid,
    "registered_at" timestamp without time zone,
    CONSTRAINT "PK_user_test" PRIMARY KEY ("registered_at")
);
""";
    }
}

