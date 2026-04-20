using System;
using System.CodeDom.Compiler;
using Socigy.OpenSource.DB.Migrations;

/*
    This code was generated using Socigy.OpenSource.DB generation tool at 04/19/2026 21:49:15 by Patrik Stohanzl - 71410855+WailedParsley36@users.noreply.github.com
*/

namespace Example.Auth.DB.Socigy.Migrations
{
    [GeneratedCode("Socigy.OpenSource.DB", "1.0.0+4b4b7b3e3a73a041a25ed198cd7123f647456229")]
    public class M_Changed_User : ILocalMigration
    {
        public string Id => _Id;

        public string UpSql => _UpSql;
        public string DownSql => _DownSql;

        public const string _Id = "Changed User";
                public const string _PreviousId = "Initial Migration";
        public string PreviousId => _PreviousId;

public const string _UpSql = """
ALTER TABLE "users" ALTER COLUMN "tag" DROP DEFAULT;
DROP SEQUENCE IF EXISTS "users_tag_seq";
ALTER TABLE "users" ADD CONSTRAINT "CHCK_5e5e586e21c34dbc815a0ca8c4320436" CHECK (LEN(email) < 25);
""";
        
public const string _DownSql = """
CREATE SEQUENCE IF NOT EXISTS "users_tag_seq" AS SMALLINT;
ALTER TABLE "users" ALTER COLUMN "tag" SET DEFAULT nextval('users_tag_seq');
ALTER TABLE "users" DROP CONSTRAINT IF EXISTS "CHCK_5e5e586e21c34dbc815a0ca8c4320436";
""";
    }
}

