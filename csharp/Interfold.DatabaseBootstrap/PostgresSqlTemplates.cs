namespace Interfold.DatabaseBootstrap;

/// <summary>
/// The SQL bodies <see cref="PostgresSeeder"/> drives an <see cref="IPostgresExecutor"/> with.
/// Kept as <c>BuildXxxSql(...)</c> helpers so the strings stay testable in isolation and so
/// callers can't accidentally hand-roll a divergent variant.
/// </summary>
/// <remarks>
/// The DO/IF blocks use <see cref="SqlEscape.Literal"/> + <c>format(... %I ... %L)</c> to
/// keep both the role name and the password safe even though the bootstrapper's password
/// alphabet excludes the apostrophe. Belt-and-braces.
/// </remarks>
public static class PostgresSqlTemplates
{
    /// <summary>
    /// Idempotent <c>CREATE ROLE</c> / <c>ALTER ROLE</c> for the admin + app users. Connect
    /// as the init user against the <c>postgres</c> database before running.
    /// </summary>
    public static string BuildRolesSql(PostgresSeedOptions o) =>
        $@"
DO $bootstrap$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{SqlEscape.Literal(o.AdminUser)}') THEN
        EXECUTE format('CREATE ROLE %I WITH SUPERUSER LOGIN PASSWORD %L', '{SqlEscape.Literal(o.AdminUser)}', '{SqlEscape.Literal(o.AdminPassword)}');
    ELSE
        EXECUTE format('ALTER ROLE %I WITH SUPERUSER LOGIN PASSWORD %L', '{SqlEscape.Literal(o.AdminUser)}', '{SqlEscape.Literal(o.AdminPassword)}');
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{SqlEscape.Literal(o.AppUser)}') THEN
        EXECUTE format('CREATE ROLE %I WITH NOSUPERUSER NOCREATEDB NOCREATEROLE LOGIN PASSWORD %L', '{SqlEscape.Literal(o.AppUser)}', '{SqlEscape.Literal(o.AppPassword)}');
    ELSE
        EXECUTE format('ALTER ROLE %I WITH NOSUPERUSER NOCREATEDB NOCREATEROLE LOGIN PASSWORD %L', '{SqlEscape.Literal(o.AppUser)}', '{SqlEscape.Literal(o.AppPassword)}');
    END IF;
END
$bootstrap$;";

    /// <summary>
    /// Probe used by <see cref="PostgresSeeder"/> to decide whether the application database
    /// already exists. Returns <c>1</c> when present, empty otherwise.
    /// </summary>
    public static string BuildDatabaseExistsProbeSql(PostgresSeedOptions o) =>
        $"SELECT 1 FROM pg_database WHERE datname = '{SqlEscape.Literal(o.DefaultDatabase)}'";

    /// <summary>Single <c>CREATE DATABASE</c> statement. Cannot be wrapped in a transaction.</summary>
    public static string BuildCreateDatabaseSql(PostgresSeedOptions o) =>
        $"CREATE DATABASE \"{o.DefaultDatabase}\" OWNER \"{o.AdminUser}\";";

    /// <summary>Single <c>ALTER DATABASE … OWNER TO</c> for the rerun-against-existing-db case.</summary>
    public static string BuildAlterDatabaseOwnerSql(PostgresSeedOptions o) =>
        $"ALTER DATABASE \"{o.DefaultDatabase}\" OWNER TO \"{o.AdminUser}\";";

    /// <summary>
    /// Idempotent schema + grants. Run against the application database as the init user.
    /// Mirrors the inline <c>schemaSql</c> block in the legacy <c>DatabaseInitPhase</c>
    /// (lines 375–406 of the pre-refactor file).
    /// </summary>
    public static string BuildSchemaSql(PostgresSeedOptions o) =>
        $@"
CREATE SCHEMA IF NOT EXISTS internal AUTHORIZATION ""{o.AdminUser}"";

CREATE TABLE IF NOT EXISTS internal.secrets (
    key          TEXT        PRIMARY KEY,
    value        TEXT        NOT NULL,
    created_by   TEXT        NOT NULL DEFAULT 'bootstrap',
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at   TIMESTAMPTZ,
    rotated_from TEXT
);
ALTER TABLE internal.secrets OWNER TO ""{o.AdminUser}"";

REVOKE ALL ON DATABASE ""{o.DefaultDatabase}"" FROM PUBLIC;
GRANT  CONNECT, TEMPORARY ON DATABASE ""{o.DefaultDatabase}"" TO ""{o.AppUser}"";

GRANT USAGE ON SCHEMA internal TO ""{o.AppUser}"";
GRANT SELECT ON internal.secrets TO ""{o.AppUser}"";

GRANT USAGE, CREATE ON SCHEMA public TO ""{o.AdminUser}"";
GRANT USAGE          ON SCHEMA public TO ""{o.AppUser}"";

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES    IN SCHEMA public TO ""{o.AppUser}"";
GRANT USAGE,  SELECT, UPDATE          ON ALL SEQUENCES IN SCHEMA public TO ""{o.AppUser}"";

ALTER DEFAULT PRIVILEGES FOR ROLE ""{o.AdminUser}"" IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES    TO ""{o.AppUser}"";
ALTER DEFAULT PRIVILEGES FOR ROLE ""{o.AdminUser}"" IN SCHEMA public
    GRANT USAGE,  SELECT, UPDATE         ON SEQUENCES TO ""{o.AppUser}"";
";

    /// <summary>
    /// Upsert template used for every <c>internal.secrets</c> row. The bind variables
    /// <c>secret_key</c> / <c>secret_value</c> map to <see cref="IPostgresExecutor.ExecScriptWithVarsAsync"/>'s
    /// var list. Both transports must turn these names into safe quoted literals (psql does
    /// it via <c>:'name'</c>; Npgsql does it via parameter binding).
    /// </summary>
    public const string UpsertSecretSql = @"
INSERT INTO internal.secrets (key, value, created_by, updated_at)
VALUES (:'secret_key', :'secret_value', 'bootstrap', now())
ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now();";

    /// <summary>
    /// Variable name used by <see cref="UpsertSecretSql"/> for the <c>internal.secrets.key</c>
    /// column. Constant so the two transport adapters agree on the bind name.
    /// </summary>
    public const string UpsertKeyVar = "secret_key";

    /// <summary>Variable name for the value column.</summary>
    public const string UpsertValueVar = "secret_value";

    /// <summary>
    /// Defence-in-depth: replace <see cref="PostgresSeedOptions.InitUser"/>'s password with
    /// the random scramble. Production callers always finish with this; tests skip it via
    /// <see cref="PostgresSeedOptions.ScrambleInitUserPassword"/>.
    /// </summary>
    public static string BuildScrambleInitUserSql(PostgresSeedOptions o, string scrambled) =>
        $"ALTER ROLE \"{o.InitUser}\" WITH PASSWORD '{SqlEscape.Literal(scrambled)}';";

    /// <summary>
    /// Probe used by <see cref="PostgresSeeder"/> for idempotency: "is the app user already
    /// configured as a non-superuser?". Connect as the app user; returns <c>1</c> on yes.
    /// </summary>
    public static string BuildAppRoleConfiguredProbeSql(PostgresSeedOptions o) =>
        $"SELECT 1 FROM pg_roles WHERE rolname = '{SqlEscape.Literal(o.AppUser)}' AND NOT rolsuper";
}
