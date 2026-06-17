namespace Interfold.DatabaseBootstrap;

/// <summary>
/// Transport-side surface that <see cref="PostgresSeeder"/> drives. Implementations decide
/// how to talk to Postgres (process-exec via <c>docker compose exec ... psql</c>, or in-process
/// via Npgsql) and how to classify transient failures during the init-mode → normal-mode
/// transition that the compose entrypoint goes through on first boot.
/// </summary>
/// <remarks>
/// All three methods accept per-call <paramref name="user"/> / <paramref name="password"/> /
/// <paramref name="database"/> because the seed flow needs to swap roles mid-orchestration
/// (init user for steps 1–3 + scramble, admin role for the secrets upsert) and swap databases
/// (postgres for cluster-level DDL, the application DB for schema + upserts). Transports
/// rebuild a connection on each call as cheaply as they can — Npgsql relies on its built-in
/// pooler, compose-exec is stateless by nature.
/// </remarks>
public interface IPostgresExecutor
{
    /// <summary>
    /// Run a DDL/DML script with no parameter bindings. Throws on failure after exhausting
    /// the transport's own transient-retry budget.
    /// </summary>
    Task ExecScriptAsync(string user, string password, string database, string sql, CancellationToken ct);

    /// <summary>
    /// Run a parameterised statement. Each transport binds the vars in its native dialect —
    /// the compose-exec transport uses <c>psql -v name=value</c> + <c>:'name'</c>, the Npgsql
    /// transport binds <c>NpgsqlParameter</c> values with matching names.
    /// </summary>
    Task ExecScriptWithVarsAsync(
        string user,
        string password,
        string database,
        string sql,
        IReadOnlyList<(string Name, string Value)> vars,
        CancellationToken ct);

    /// <summary>
    /// Run a scalar-yielding query. Returns the first column of the first row as a string
    /// (or <c>null</c> when no row is returned). Used for idempotency probes — never wraps
    /// transient errors as success, so the caller must guard.
    /// </summary>
    Task<string?> ExecScalarAsync(string user, string password, string database, string sql, CancellationToken ct);
}
