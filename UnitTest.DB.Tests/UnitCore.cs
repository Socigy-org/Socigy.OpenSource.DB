using Npgsql;
using System.Text.Json;

namespace UnitTest.DB.Tests;

/// <summary>
/// Singleton that bootstraps a real PostgreSQL connection and ensures the test
/// schema exists before any tests run.  Connection string is read from
/// <c>appsettings.json</c> (key: ConnectionStrings.TestDb.Default).
/// </summary>
public static class UnitCore
{
    private static string? _connectionString;
    private static bool _initialized;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public static string ConnectionString => _connectionString
        ?? throw new InvalidOperationException("UnitCore not initialised — call InitializeAsync() first.");

    public static NpgsqlConnection CreateConnection() => new(_connectionString!);

    public static async Task InitializeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_initialized) return;
            _initialized = true;

            _connectionString = ReadConnectionString();
            await EnsureSchemaAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string ReadConnectionString()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement
            .GetProperty("ConnectionStrings")
            .GetProperty("TestDb")
            .GetProperty("Default")
            .GetString()
            ?? throw new InvalidOperationException("Connection string not found in appsettings.json");
    }

    private static async Task EnsureSchemaAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaSql;
        await cmd.ExecuteNonQueryAsync();
    }

    // DDL that mirrors the attributes on the test models.
    // The 'CREATE … IF NOT EXISTS' and 'ON CONFLICT DO NOTHING' make re-runs idempotent.
    internal const string SchemaSql = @"
        -- ---------------------------------------------------------------
        -- Sequence for TestCounter.Seq  (name = table_column_seq)
        -- ---------------------------------------------------------------
        CREATE SEQUENCE IF NOT EXISTS ""test_counters_seq_seq"" AS INTEGER START 1;

        -- ---------------------------------------------------------------
        -- test_items  (basic CRUD table with DB-side defaults)
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ""test_items"" (
            ""id""          UUID        NOT NULL DEFAULT gen_random_uuid(),
            ""name""        TEXT        NOT NULL DEFAULT '',
            ""priority""    INTEGER     NOT NULL DEFAULT 0,
            ""created_at""  TIMESTAMP   NOT NULL DEFAULT NOW(),
            PRIMARY KEY (""id"")
        );

        -- ---------------------------------------------------------------
        -- test_counters  (AutoIncrement column)
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ""test_counters"" (
            ""id""         UUID        NOT NULL,
            ""seq""        INTEGER     NOT NULL DEFAULT nextval('""test_counters_seq_seq""'),
            ""label""      TEXT        NOT NULL DEFAULT '',
            ""created_tz"" TIMESTAMPTZ NOT NULL DEFAULT now(),
            ""long_join_column_name_used_to_exceed_sixty_three_alias_boundary"" TEXT,
            PRIMARY KEY (""id"")
        );
        -- The table may pre-exist from an earlier run without these columns (CREATE IF NOT EXISTS won't add them).
        ALTER TABLE ""test_counters"" ADD COLUMN IF NOT EXISTS ""created_tz"" TIMESTAMPTZ NOT NULL DEFAULT now();
        ALTER TABLE ""test_counters"" ADD COLUMN IF NOT EXISTS ""long_join_column_name_used_to_exceed_sixty_three_alias_boundary"" TEXT;

        -- ---------------------------------------------------------------
        -- test_json_items  (raw JSON + typed JSON columns)
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ""test_json_items"" (
            ""id""       UUID  NOT NULL DEFAULT gen_random_uuid(),
            ""name""     TEXT  NOT NULL DEFAULT '',
            ""raw_data"" JSONB,
            ""payload""  JSONB,
            PRIMARY KEY (""id"")
        );

        -- ---------------------------------------------------------------
        -- test_convertor_items  (value convertor)
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ""test_convertor_items"" (
            ""id""    UUID    NOT NULL DEFAULT gen_random_uuid(),
            ""label"" TEXT    NOT NULL DEFAULT '',
            ""value"" INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (""id"")
        );

        -- ---------------------------------------------------------------
        -- test_roles  (enum reference table for FlaggedEnum)
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ""test_roles"" (
            ""id""   INTEGER NOT NULL,
            ""name"" TEXT    NOT NULL,
            PRIMARY KEY (""id"")
        );
        INSERT INTO ""test_roles"" (""id"", ""name"") VALUES
            (1, 'Reader'), (2, 'Writer'), (4, 'Moderator'), (8, 'Admin')
        ON CONFLICT (""id"") DO UPDATE SET ""name"" = EXCLUDED.""name"";

        -- ---------------------------------------------------------------
        -- test_users  (FlaggedEnum owner)
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ""test_users"" (
            ""id""       UUID NOT NULL DEFAULT gen_random_uuid(),
            ""username"" TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (""id"")
        );

        -- ---------------------------------------------------------------
        -- test_users_test_roles  (junction table auto-generated by [FlaggedEnum])
        -- column names match the source-generator naming convention:
        --   main PK FK  = {mainTable}_{pkDbName}    => test_users_id
        --   enum FK     = {enumTable}_id             => test_roles_id
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ""test_users_test_roles"" (
            ""test_users_id""  UUID    NOT NULL,
            ""test_roles_id""  INTEGER NOT NULL,
            PRIMARY KEY (""test_users_id"", ""test_roles_id""),
            FOREIGN KEY (""test_users_id"")  REFERENCES ""test_users""(""id"") ON DELETE CASCADE,
            FOREIGN KEY (""test_roles_id"")  REFERENCES ""test_roles""(""id"")
        );

        -- ---------------------------------------------------------------
        -- test_types  (broad type coverage + nullable column)
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ""test_types"" (
            ""id""             UUID      NOT NULL DEFAULT gen_random_uuid(),
            ""is_active""      BOOLEAN   NOT NULL DEFAULT FALSE,
            ""nullable_value"" INTEGER,
            ""amount""         NUMERIC   NOT NULL DEFAULT 0,
            ""when""           TIMESTAMP NOT NULL DEFAULT NOW(),
            ""note""           TEXT,
            ""small_byte""     SMALLINT  NOT NULL DEFAULT 0,
            ""signed_byte""    SMALLINT  NOT NULL DEFAULT 0,
            ""big""            NUMERIC   NOT NULL DEFAULT 0,
            PRIMARY KEY (""id"")
        );
        -- The table may pre-exist from an earlier run without these columns (CREATE IF NOT EXISTS won't add them).
        ALTER TABLE ""test_types"" ADD COLUMN IF NOT EXISTS ""small_byte""  SMALLINT NOT NULL DEFAULT 0;
        ALTER TABLE ""test_types"" ADD COLUMN IF NOT EXISTS ""signed_byte"" SMALLINT NOT NULL DEFAULT 0;
        ALTER TABLE ""test_types"" ADD COLUMN IF NOT EXISTS ""big""         NUMERIC  NOT NULL DEFAULT 0;

        -- ---------------------------------------------------------------
        -- char_items  (char column stored as character(1))
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ""char_items"" (
            ""id""      UUID         NOT NULL DEFAULT gen_random_uuid(),
            ""grade""   CHARACTER(1) NOT NULL DEFAULT ' ',
            ""initial"" CHARACTER(1),
            PRIMARY KEY (""id"")
        );

        -- ---------------------------------------------------------------
        -- test_secrets  ([Encrypted] columns stored as bytea ciphertext)
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ""test_secrets"" (
            ""id""        UUID NOT NULL DEFAULT gen_random_uuid(),
            ""owner""     TEXT NOT NULL DEFAULT '',
            ""ssn""       BYTEA,
            ""pin""       BYTEA,
            ""token""     BYTEA,
            ""issued_at"" BYTEA,
            ""note""      BYTEA,
            ""manual""    BYTEA,
            PRIMARY KEY (""id"")
        );

        -- ---------------------------------------------------------------
        -- test_required  (entity with a `required` member — issue #2)
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ""test_required"" (
            ""id""     UUID NOT NULL DEFAULT gen_random_uuid(),
            ""label""  TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (""id"")
        );

        -- ---------------------------------------------------------------
        -- test_convertor_pk_items  ([ValueConvertor] on the PRIMARY KEY)
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ""test_convertor_pk_items"" (
            ""code"" TEXT NOT NULL,
            ""note"" TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (""code"")
        );

        -- ---------------------------------------------------------------
        -- test_enum_convertor_items  (enum stored as text via a custom convertor)
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ""test_enum_convertor_items"" (
            ""id""     UUID NOT NULL DEFAULT gen_random_uuid(),
            ""status"" TEXT NOT NULL DEFAULT 'Pending',
            PRIMARY KEY (""id"")
        );

        -- ---------------------------------------------------------------
        -- test_enum_pk_convertor_items  (enum PRIMARY KEY via a type-changing convertor → text)
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ""test_enum_pk_convertor_items"" (
            ""status"" TEXT NOT NULL,
            ""note""   TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (""status"")
        );

        -- ---------------------------------------------------------------
        -- test_inherited  (column inherited from a base class + a column in a second partial)
        -- ---------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS ""test_inherited"" (
            ""id""         UUID NOT NULL DEFAULT gen_random_uuid(),
            ""name""       TEXT NOT NULL DEFAULT '',
            ""score""      INTEGER NOT NULL DEFAULT 0,
            ""created_by"" TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (""id"")
        );
    ";
}
