using System;
using System.CodeDom.Compiler;
using Socigy.OpenSource.DB.Migrations;

/*
    This code was generated using Socigy.OpenSource.DB generation tool at 04/22/2026 13:41:01 by Patrik Stohanzl - 71410855+WailedParsley36@users.noreply.github.com
*/

namespace Example.Auth.DB.Socigy.Migrations
{
    [GeneratedCode("Socigy.OpenSource.DB", "1.0.0+bff7c2df91148854e14d0745a6879ab1190791a7")]
    public class M_202604221341__Initial_Migration_90b66f2b06 : ILocalMigration
    {
        public string Id => _Id;

        public string UpSql => _UpSql;
        public string DownSql => _DownSql;

        public const string _Id = "202604221341__Initial_Migration_90b66f2b06";
        #nullable enable
        public string? PreviousId => null;
        #nullable disable

public const string _UpSql = """
CREATE TABLE "user_visibility" (
    "id" smallint,
    "value" text,
    "description" text,
    CONSTRAINT "PK_user_visibility" PRIMARY KEY ("id")
);
INSERT INTO "user_visibility" ("id", "value", "description") VALUES (0, 'Public', 'This will make the user visible to everyone');
INSERT INTO "user_visibility" ("id", "value", "description") VALUES (1, 'CirclesOnly', NULL);
INSERT INTO "user_visibility" ("id", "value", "description") VALUES (2, 'CustomCircles', NULL);
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
CREATE TABLE "users" (
    "id" uuid DEFAULT gen_random_uuid(),
    "username" text,
    "tag" smallint,
    "icon_url" text,
    "email" character varying(10),
    "email_verified" boolean,
    "registration_complete" boolean,
    "phone_number" text,
    "first_name" text,
    "last_name" text,
    "birth_date" timestamp without time zone,
    "is_child" boolean,
    "parent_id" uuid,
    "visibility" smallint,
    CONSTRAINT "CHCK_f2d6aa1d5a5c4878913cd18194603183" CHECK (LEN(email) < 25),
    CONSTRAINT "PK_users" PRIMARY KEY ("id")
);
CREATE TABLE "users_parent_role" (
    "user_id" uuid,
    "user_role_id" smallint,
    "assigned_at" timestamp without time zone DEFAULT timezone('utc', now()),
    CONSTRAINT "PK_users_parent_role" PRIMARY KEY ("user_id", "user_role_id")
);
CREATE TABLE "courses" (
    "id" uuid,
    "name" text DEFAULT 'DEFAULT NAME',
    "created_at" timestamp without time zone DEFAULT timezone('utc', now()),
    CONSTRAINT "PK_courses" PRIMARY KEY ("id")
);
CREATE TABLE "user_course" (
    "user_id" uuid,
    "course_id" uuid,
    "registered_at" timestamp without time zone,
    CONSTRAINT "PK_user_course" PRIMARY KEY ("user_id", "course_id")
);
CREATE TABLE "user_course_agreement" (
    "user_id" uuid,
    "course_id" uuid
);
CREATE TABLE "user_login" (
    "id" uuid,
    "username" text DEFAULT 'Tvoje máma',
    "password_hash" text,
    CONSTRAINT "PK_user_login" PRIMARY KEY ("id")
);
CREATE SEQUENCE IF NOT EXISTS "_scg_migrations_id_seq" AS BIGINT;
CREATE TABLE "_scg_migrations" (
    "id" bigint DEFAULT nextval('_scg_migrations_id_seq'),
    "human_id" text,
    "applied_at" timestamp without time zone,
    "is_rollback" boolean DEFAULT false,
    "executed_by" text,
    CONSTRAINT "PK__scg_migrations" PRIMARY KEY ("id")
);
ALTER TABLE "users_user_role" ADD CONSTRAINT "FK_users_id" FOREIGN KEY ("users_id") REFERENCES "users" ("id") ON DELETE CASCADE;
ALTER TABLE "users_user_role" ADD CONSTRAINT "FK_user_role_id" FOREIGN KEY ("user_role_id") REFERENCES "user_role" ("id") ON DELETE CASCADE;
ALTER TABLE "users" ADD CONSTRAINT "FK_Visibility" FOREIGN KEY ("visibility") REFERENCES "user_visibility" ("id");
ALTER TABLE "users_parent_role" ADD CONSTRAINT "FK_UserId" FOREIGN KEY ("user_id") REFERENCES "users" ("id") ON DELETE CASCADE;
ALTER TABLE "users_parent_role" ADD CONSTRAINT "FK_UserRoleId" FOREIGN KEY ("user_role_id") REFERENCES "user_role" ("id") ON DELETE CASCADE;
ALTER TABLE "user_course" ADD CONSTRAINT "FK_UserId" FOREIGN KEY ("user_id") REFERENCES "users" ("id");
ALTER TABLE "user_course" ADD CONSTRAINT "FK_CourseId" FOREIGN KEY ("course_id") REFERENCES "courses" ("id");
ALTER TABLE "user_course_agreement" ADD CONSTRAINT "FK_UserId_CourseId" FOREIGN KEY ("user_id", "course_id") REFERENCES "user_course" ("user_id", "course_id");
""";
        
public const string _DownSql = """
ALTER TABLE "user_course_agreement" DROP CONSTRAINT IF EXISTS "FK_UserId_CourseId";
ALTER TABLE "user_course" DROP CONSTRAINT IF EXISTS "FK_CourseId";
ALTER TABLE "user_course" DROP CONSTRAINT IF EXISTS "FK_UserId";
ALTER TABLE "users_parent_role" DROP CONSTRAINT IF EXISTS "FK_UserRoleId";
ALTER TABLE "users_parent_role" DROP CONSTRAINT IF EXISTS "FK_UserId";
ALTER TABLE "users" DROP CONSTRAINT IF EXISTS "FK_Visibility";
ALTER TABLE "users_user_role" DROP CONSTRAINT IF EXISTS "FK_user_role_id";
ALTER TABLE "users_user_role" DROP CONSTRAINT IF EXISTS "FK_users_id";
DROP SEQUENCE IF EXISTS "_scg_migrations_id_seq";
DROP TABLE IF EXISTS "_scg_migrations" CASCADE;
DROP TABLE IF EXISTS "user_login" CASCADE;
DROP TABLE IF EXISTS "user_course_agreement" CASCADE;
DROP TABLE IF EXISTS "user_course" CASCADE;
DROP TABLE IF EXISTS "courses" CASCADE;
DROP TABLE IF EXISTS "users_parent_role" CASCADE;
DROP TABLE IF EXISTS "users" CASCADE;
DROP TABLE IF EXISTS "users_user_role" CASCADE;
DROP TABLE IF EXISTS "user_role" CASCADE;
DROP TABLE IF EXISTS "user_visibility" CASCADE;
""";
    }
}

