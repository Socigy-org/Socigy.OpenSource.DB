using System;
using System.CodeDom.Compiler;
using Socigy.OpenSource.DB.Migrations;

/*
    This code was generated using Socigy.OpenSource.DB generation tool at 04/18/2026 21:13:16 by Patrik Stohanzl - 71410855+WailedParsley36@users.noreply.github.com
*/

namespace Example.User.DB.Socigy.Migrations
{
    [GeneratedCode("Socigy.OpenSource.DB", "1.0.0+4b4b7b3e3a73a041a25ed198cd7123f647456229")]
    public class M_Initial_Migration : ILocalMigration
    {
        public string Id => _Id;

        public string UpSql => _UpSql;
        public string DownSql => _DownSql;

        public const string _Id = "Initial Migration";
        #nullable enable
        public string? PreviousId => null;
        #nullable disable

public const string _UpSql = """
CREATE TABLE "user_login" (
    "id" uuid,
    "email" text,
    "username" text,
    "first_name" text,
    "last_name" text,
    CONSTRAINT "UQ_Email" UNIQUE ("email"),
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
""";
        
public const string _DownSql = """
DROP SEQUENCE IF EXISTS "_scg_migrations_id_seq";
DROP TABLE IF EXISTS "_scg_migrations" CASCADE;
DROP TABLE IF EXISTS "user_login" CASCADE;
""";
    }
}

