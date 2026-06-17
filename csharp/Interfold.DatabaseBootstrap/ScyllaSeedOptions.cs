namespace Interfold.DatabaseBootstrap;

/// <summary>
/// Input contract for <see cref="ScyllaSeeder.BootstrapAsync"/>. The built-in
/// <c>cassandra / cassandra</c> superuser is hard-coded inside <see cref="ScyllaSeeder"/>
/// (driven from a default-credentials constant) so callers don't have to repeat it.
/// </summary>
/// <param name="AppUser">Non-superuser role created here. <c>SUPERUSER = false</c>,
/// <c>LOGIN = true</c>.</param>
/// <param name="AppPassword">Password for <see cref="AppUser"/>.</param>
/// <param name="AdminUser">Superuser role created here. <c>SUPERUSER = true</c>,
/// <c>LOGIN = true</c>. Convention <c>&lt;app&gt;_admin</c>.</param>
/// <param name="AdminPassword">Password for <see cref="AdminUser"/>.</param>
/// <param name="LockDefaultCassandra">When true (and neither user equals <c>cassandra</c>),
/// the seeder finishes by <c>ALTER ROLE 'cassandra' WITH PASSWORD = '&lt;random&gt;' AND LOGIN = false</c>.
/// Production + tests both pass <c>true</c>; the bool stays here so a future operator who
/// genuinely wants <c>cassandra</c> as the live account can flip it.</param>
public sealed record ScyllaSeedOptions(
    string AppUser,
    string AppPassword,
    string AdminUser,
    string AdminPassword,
    bool LockDefaultCassandra);
