namespace Interfold.DatabaseBootstrap;

/// <summary>
/// The CQL bodies <see cref="ScyllaSeeder"/> drives an <see cref="IScyllaExecutor"/> with.
/// Like the Postgres templates, kept as <c>BuildXxxCql</c> helpers so the strings are testable
/// in isolation.
/// </summary>
public static class ScyllaCqlTemplates
{
    /// <summary>Cassandra/Scylla built-in superuser before lockdown.</summary>
    public const string DefaultUser = "cassandra";

    /// <summary>Default password for <see cref="DefaultUser"/> on a fresh data volume.</summary>
    public const string DefaultPassword = "cassandra";

    /// <summary>
    /// <c>CREATE ROLE IF NOT EXISTS</c> for the admin superuser. Driven as the
    /// <see cref="DefaultUser"/> session — never as the admin itself, because Cassandra/Scylla
    /// enforces "you can't alter your own superuser status".
    /// </summary>
    public static string BuildCreateAdminRoleCql(ScyllaSeedOptions o) =>
        $"CREATE ROLE IF NOT EXISTS '{SqlEscape.Literal(o.AdminUser)}' " +
        $"WITH PASSWORD = '{SqlEscape.Literal(o.AdminPassword)}' " +
        "AND SUPERUSER = true AND LOGIN = true";

    /// <summary><c>CREATE ROLE IF NOT EXISTS</c> for the non-superuser app role.</summary>
    public static string BuildCreateAppRoleCql(ScyllaSeedOptions o) =>
        $"CREATE ROLE IF NOT EXISTS '{SqlEscape.Literal(o.AppUser)}' " +
        $"WITH PASSWORD = '{SqlEscape.Literal(o.AppPassword)}' " +
        "AND SUPERUSER = false AND LOGIN = true";

    /// <summary>
    /// Lockdown statement run last as the <see cref="DefaultUser"/> session. Omits any
    /// <c>SUPERUSER</c> clause to avoid the self-alter restriction (cassandra is still a
    /// superuser at this point, so demoting itself would error).
    /// </summary>
    public static string BuildLockDefaultUserCql(string scrambledPassword) =>
        $"ALTER ROLE '{DefaultUser}' WITH PASSWORD = '{SqlEscape.Literal(scrambledPassword)}' AND LOGIN = false";

    /// <summary>
    /// Idempotency probe: <c>LIST ROLES OF '&lt;admin&gt;'</c>. A successful response that
    /// contains the admin name means the previous seed pass got past role creation.
    /// </summary>
    public static string BuildListAdminRoleCql(ScyllaSeedOptions o) =>
        $"LIST ROLES OF '{SqlEscape.Literal(o.AdminUser)}'";
}
