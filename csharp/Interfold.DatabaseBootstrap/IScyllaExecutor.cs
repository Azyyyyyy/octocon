namespace Interfold.DatabaseBootstrap;

/// <summary>
/// Transport-side surface that <see cref="ScyllaSeeder"/> drives. Same shape as
/// <see cref="IPostgresExecutor"/> but two methods: a throwing <c>ExecCqlAsync</c> for
/// statements where failure is fatal and a <c>TryExecCqlAsync</c> for the "does role X
/// exist?" probes where the caller wants the output regardless of exit status.
/// </summary>
public interface IScyllaExecutor
{
    /// <summary>Run a CQL statement as the given role. Throws on failure.</summary>
    Task ExecCqlAsync(string user, string password, string cql, CancellationToken ct);

    /// <summary>
    /// Run a CQL statement and return the captured success-flag + stdout. Used for
    /// probes that need the output even when the statement fails (e.g. an auth failure
    /// while the cluster is still bootstrapping is treated as "not yet ready", not as
    /// "fatal error").
    /// </summary>
    Task<ScyllaExecResult> TryExecCqlAsync(string user, string password, string cql, CancellationToken ct);
}

/// <summary>
/// Captured result of an <see cref="IScyllaExecutor.TryExecCqlAsync"/> call. The
/// <paramref name="Output"/> is what the underlying transport collected — for compose-exec
/// this is <c>cqlsh</c>'s stdout, for the DataStax driver it's a flattened row dump.
/// </summary>
public readonly record struct ScyllaExecResult(bool Succeeded, string Output);
