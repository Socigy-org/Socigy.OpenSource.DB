using System;
using System.CodeDom.Compiler;
using Socigy.OpenSource.DB.Migrations;

/*
    This code was generated using Socigy.OpenSource.DB generation tool at 04/19/2026 22:24:22 by Patrik Stohanzl - 71410855+WailedParsley36@users.noreply.github.com
*/

namespace Example.Auth.DB.Socigy.Migrations
{
    [GeneratedCode("Socigy.OpenSource.DB", "1.0.0+4b4b7b3e3a73a041a25ed198cd7123f647456229")]
    public class M_Changes : ILocalMigration
    {
        public string Id => _Id;

        public string UpSql => _UpSql;
        public string DownSql => _DownSql;

        public const string _Id = "Changes";
                public const string _PreviousId = "Changed User and Added a new Enum";
        public string PreviousId => _PreviousId;

public const string _UpSql = """
ALTER TABLE "users_parent_role" DROP CONSTRAINT IF EXISTS "FK_UserId";
ALTER TABLE "users_parent_role" DROP CONSTRAINT IF EXISTS "FK_UserRoleId";
ALTER TABLE "users_parent_role" ADD CONSTRAINT "FK_UserId" FOREIGN KEY ("user_id") REFERENCES "users" ("id") ON DELETE CASCADE;
ALTER TABLE "users_parent_role" ADD CONSTRAINT "FK_UserRoleId" FOREIGN KEY ("user_role_id") REFERENCES "user_role" ("id") ON DELETE CASCADE;
""";
        
public const string _DownSql = """
ALTER TABLE "users_parent_role" ADD CONSTRAINT "FK_UserId" FOREIGN KEY ("user_id") REFERENCES "users" ("id");
ALTER TABLE "users_parent_role" ADD CONSTRAINT "FK_UserRoleId" FOREIGN KEY ("user_role_id") REFERENCES "user_role" ("id");
ALTER TABLE "users_parent_role" DROP CONSTRAINT IF EXISTS "FK_UserId";
ALTER TABLE "users_parent_role" DROP CONSTRAINT IF EXISTS "FK_UserRoleId";
""";
    }
}

