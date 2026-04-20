using System;
using System.CodeDom.Compiler;
using Socigy.OpenSource.DB.Migrations;

/*
    This code was generated using Socigy.OpenSource.DB generation tool at 04/19/2026 22:02:56 by Patrik Stohanzl - 71410855+WailedParsley36@users.noreply.github.com
*/

namespace Example.Auth.DB.Socigy.Migrations
{
    [GeneratedCode("Socigy.OpenSource.DB", "1.0.0+4b4b7b3e3a73a041a25ed198cd7123f647456229")]
    public class M_Changed_User_and_Added_a_new_Enum : ILocalMigration
    {
        public string Id => _Id;

        public string UpSql => _UpSql;
        public string DownSql => _DownSql;

        public const string _Id = "Changed User and Added a new Enum";
                public const string _PreviousId = "Changed User";
        public string PreviousId => _PreviousId;

public const string _UpSql = """
CREATE TABLE "user_role" (
    "id" smallint,
    "value" text,
    "description" text,
    CONSTRAINT "PK_user_role" PRIMARY KEY ("id")
);
INSERT INTO "user_role" ("id", "value", "description") VALUES (1, 'User', NULL);
INSERT INTO "user_role" ("id", "value", "description") VALUES (2, 'Admin', NULL);
INSERT INTO "user_role" ("id", "value", "description") VALUES (4, 'Developer', NULL);
INSERT INTO "user_role" ("id", "value", "description") VALUES (8, 'Reviewer', NULL);
CREATE TABLE "users_user_role" (
    "users_id" uuid NOT NULL,
    "user_role_id" smallint NOT NULL,
    CONSTRAINT "PK_users_user_role" PRIMARY KEY ("users_id", "user_role_id")
);
CREATE TABLE "users_parent_role" (
    "user_id" uuid,
    "user_role_id" smallint,
    "assigned_at" timestamp without time zone DEFAULT timezone('utc', now()),
    CONSTRAINT "PK_users_parent_role" PRIMARY KEY ("user_id", "user_role_id")
);
ALTER TABLE "courses" ALTER COLUMN "name" SET DEFAULT 'DEFAULT NAME';
ALTER TABLE "user_login" ALTER COLUMN "username" SET DEFAULT 'Tvoje máma';
ALTER TABLE "users_user_role" ADD CONSTRAINT "FK_users_id" FOREIGN KEY ("users_id") REFERENCES "users" ("id") ON DELETE CASCADE;
ALTER TABLE "users_user_role" ADD CONSTRAINT "FK_user_role_id" FOREIGN KEY ("user_role_id") REFERENCES "user_role" ("id") ON DELETE CASCADE;
ALTER TABLE "users_parent_role" ADD CONSTRAINT "FK_UserId" FOREIGN KEY ("user_id") REFERENCES "users" ("id");
ALTER TABLE "users_parent_role" ADD CONSTRAINT "FK_UserRoleId" FOREIGN KEY ("user_role_id") REFERENCES "user_role" ("id");
""";
        
public const string _DownSql = """
ALTER TABLE "users_parent_role" DROP CONSTRAINT IF EXISTS "FK_UserRoleId";
ALTER TABLE "users_parent_role" DROP CONSTRAINT IF EXISTS "FK_UserId";
ALTER TABLE "users_user_role" DROP CONSTRAINT IF EXISTS "FK_user_role_id";
ALTER TABLE "users_user_role" DROP CONSTRAINT IF EXISTS "FK_users_id";
ALTER TABLE "user_login" ALTER COLUMN "username" DROP DEFAULT;
ALTER TABLE "courses" ALTER COLUMN "name" DROP DEFAULT;
DROP TABLE IF EXISTS "users_parent_role" CASCADE;
DROP TABLE IF EXISTS "users_user_role" CASCADE;
DROP TABLE IF EXISTS "user_role" CASCADE;
""";
    }
}

