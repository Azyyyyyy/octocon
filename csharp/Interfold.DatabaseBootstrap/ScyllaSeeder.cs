using System.Security.Cryptography;

namespace Interfold.DatabaseBootstrap;

/// <summary>
/// Orchestrates the three Scylla / Cassandra seed steps end-to-end: mint an admin role,
/// mint the app role, lock out the default <c>cassandra</c> account. All work is driven via
/// the built-in <c>cassandra / cassandra</c> session because Cassandra forbids a role from
/// altering its own superuser status — so the role doing the work cannot be the role being
/// changed.
/// </summary>
public static class ScyllaSeeder
{
    private const string ScrambleAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>
    /// Runs admin role creation, app role creation, and the default-account lockdown.
    /// Idempotent: when the admin role is already present the orchestrator short-circuits.
    /// </summary>
    public static async Task BootstrapAsync(
        IScyllaExecutor executor,
        ScyllaSeedOptions options,
        IDatabaseInitLogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (await AdminRoleAlreadyConfiguredAsync(executor, options, ct).ConfigureAwait(false))
        {
            // Keyword "scylla already initialised" is what BootstrapIdempotenceTests grep for
            // when verifying a short-circuit on a second-bootstrap rerun. The role-name suffix is
            // informational — it lets operators eyeball the cluster state in the phase log.
            logger.Info($"    scylla already initialised (state probe: admin role '{options.AdminUser}' present); skipping admin bootstrap");
            return;
        }

        logger.Info(
            $"    creating scylla roles: app='{options.AppUser}' (DML-only), " +
            $"admin='{options.AdminUser}' (superuser), locking default cassandra...");

        await executor.ExecCqlAsync(
            ScyllaCqlTemplates.DefaultUser, ScyllaCqlTemplates.DefaultPassword,
            ScyllaCqlTemplates.BuildCreateAdminRoleCql(options), ct).ConfigureAwait(false);

        await executor.ExecCqlAsync(
            ScyllaCqlTemplates.DefaultUser, ScyllaCqlTemplates.DefaultPassword,
            ScyllaCqlTemplates.BuildCreateAppRoleCql(options), ct).ConfigureAwait(false);

        if (options.LockDefaultCassandra
            && !string.Equals(options.AppUser, ScyllaCqlTemplates.DefaultUser, StringComparison.Ordinal)
            && !string.Equals(options.AdminUser, ScyllaCqlTemplates.DefaultUser, StringComparison.Ordinal))
        {
            // Random throwaway password is belt + braces — LOGIN = false is the real
            // protection. Never persist this anywhere; future callers should authenticate
            // as the freshly-minted admin role instead.
            var scrambled = RandomNumberGenerator.GetString(ScrambleAlphabet, 48);
            await executor.ExecCqlAsync(
                ScyllaCqlTemplates.DefaultUser, ScyllaCqlTemplates.DefaultPassword,
                ScyllaCqlTemplates.BuildLockDefaultUserCql(scrambled), ct).ConfigureAwait(false);
        }

        logger.Info(
            $"    scylla bootstrapped: app='{options.AppUser}' (non-superuser), " +
            $"admin='{options.AdminUser}' (superuser), default cassandra " +
            (options.LockDefaultCassandra ? "locked" : "left intact"));
    }

    private static async Task<bool> AdminRoleAlreadyConfiguredAsync(
        IScyllaExecutor executor, ScyllaSeedOptions options, CancellationToken ct)
    {
        // First try the default cassandra session — it's still usable until lockdown finishes
        // on the very first seed pass; on a subsequent rerun (after lockdown) the auth will
        // fail and we fall back to the admin role's own credentials.
        var asDefault = await executor.TryExecCqlAsync(
            ScyllaCqlTemplates.DefaultUser, ScyllaCqlTemplates.DefaultPassword,
            ScyllaCqlTemplates.BuildListAdminRoleCql(options), ct).ConfigureAwait(false);
        if (asDefault.Succeeded && asDefault.Output.Contains(options.AdminUser, StringComparison.Ordinal))
        {
            return true;
        }

        var asAdmin = await executor.TryExecCqlAsync(
            options.AdminUser, options.AdminPassword,
            ScyllaCqlTemplates.BuildListAdminRoleCql(options), ct).ConfigureAwait(false);
        return asAdmin.Succeeded;
    }
}
