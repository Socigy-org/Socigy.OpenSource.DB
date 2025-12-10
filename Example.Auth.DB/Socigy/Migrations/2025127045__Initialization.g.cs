using System;
using System.CodeDom.Compiler;
using Socigy.OpenSource.DB.Migrations;

/*
    This code was generated using Socigy.OpenSource.DB DB generation tool at 12/07/2025 00:45:44
*/

namespace Example.Auth.DB.Socigy.Migrations
{
    [GeneratedCode("Socigy.OpenSource.DB", "1.0.0")]
    public class M_2025127045__Initialization : IMigration
    {
        public string Id => _Id;

        public string UpSql => _UpSql;
        public string DownSql => _DownSql;

        public const string _Id = "2025127045__Initialization";
        #nullable enable
        public string? PreviousId => null;
        #nullable disable

public const string _UpSql = """
CREATE TABLE ""user_visibility"" (
    ""id"" smallint,
    ""value"" text,
    ""description"" text,
    CONSTRAINT ""PK_user_visibility"" PRIMARY KEY (""id"")
);
INSERT INTO ""user_visibility"" (""id"", ""value"", ""description"") VALUES (0, 'Public', 'This will make the user visible to everyone');
INSERT INTO ""user_visibility"" (""id"", ""value"", ""description"") VALUES (1, 'CirclesOnly', NULL);
INSERT INTO ""user_visibility"" (""id"", ""value"", ""description"") VALUES (2, 'CustomCircles', NULL);
CREATE TABLE ""users"" (
    ""id"" uuid,
    ""username"" text,
    ""tag"" smallint,
    ""icon_url"" text,
    ""email"" text,
    ""email_verified"" boolean,
    ""registration_complete"" boolean,
    ""phone_number"" text,
    ""first_name"" text,
    ""last_name"" text,
    ""birth_date"" timestamp without time zone,
    ""is_child"" boolean,
    ""parent_id"" uuid,
    ""visibility"" smallint,
    CONSTRAINT ""PK_users"" PRIMARY KEY (""id"")
);
CREATE TABLE ""courses"" (
    ""id"" uuid,
    ""name"" text,
    ""created_at"" timestamp without time zone DEFAULT timezone('utc', now()),
    CONSTRAINT ""PK_courses"" PRIMARY KEY (""id"")
);
CREATE TABLE ""user_course"" (
    ""user_id"" uuid,
    ""course_id"" uuid,
    ""registered_at"" timestamp without time zone,
    CONSTRAINT ""PK_user_course"" PRIMARY KEY (""user_id"", ""course_id"")
);
CREATE TABLE ""user_course_agreement"" (
    ""user_id"" uuid,
    ""course_id"" uuid
);
CREATE TABLE ""user_login"" (
    ""id"" uuid,
    ""username"" text,
    ""password_hash"" text,
    CONSTRAINT ""PK_user_login"" PRIMARY KEY (""id"")
);
ALTER TABLE ""users"" ADD CONSTRAINT ""FK_Visibility"" FOREIGN KEY (""visibility"") REFERENCES ""user_visibility"" (""id"");
ALTER TABLE ""user_course"" ADD CONSTRAINT ""FK_UserId"" FOREIGN KEY (""user_id"") REFERENCES ""users"" (""id"");
ALTER TABLE ""user_course"" ADD CONSTRAINT ""FK_CourseId"" FOREIGN KEY (""course_id"") REFERENCES ""courses"" (""id"");
ALTER TABLE ""user_course_agreement"" ADD CONSTRAINT ""FK_UserId_CourseId"" FOREIGN KEY (""user_id"", ""course_id"") REFERENCES ""user_course"" (""user_id"", ""course_id"");
""";
        
public const string _DownSql = """
ALTER TABLE ""user_course_agreement"" DROP CONSTRAINT IF EXISTS ""FK_UserId_CourseId"";
ALTER TABLE ""user_course"" DROP CONSTRAINT IF EXISTS ""FK_CourseId"";
ALTER TABLE ""user_course"" DROP CONSTRAINT IF EXISTS ""FK_UserId"";
ALTER TABLE ""users"" DROP CONSTRAINT IF EXISTS ""FK_Visibility"";
DROP TABLE IF EXISTS ""user_login"" CASCADE;
DROP TABLE IF EXISTS ""user_course_agreement"" CASCADE;
DROP TABLE IF EXISTS ""user_course"" CASCADE;
DROP TABLE IF EXISTS ""courses"" CASCADE;
DROP TABLE IF EXISTS ""users"" CASCADE;
DROP TABLE IF EXISTS ""user_visibility"" CASCADE;
""";
    }
}

