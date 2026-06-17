namespace Interfold.Contracts.Configuration;

/// <summary>
/// Database and persistence configuration for Scylla, PostgreSQL, and retry logic.
/// Binds from environment variables with OCTOCON_ prefix:
///   - OCTOCON_PERSISTENCE (scylla-postgres or inmemory)
///   - OCTOCON_SCYLLA_KEYSPACE (per-instance region/keyspace; default: nam)
///   - OCTOCON_POSTGRES_CONNECTION
///   - OCTOCON_SINGLE_SCYLLA_INSTANCE
///   - OCTOCON_DB_RETRY_* (retry strategy parameters)
///   - OCTOCON_HYDRATION_MAX_CONCURRENCY
///
/// Scylla connection details (contact_points, datacenter, username, password, keyspace)
/// and admin credentials are stored in internal.secrets (PostgreSQL) and read directly
/// by services via ISecretsStore. The keyspace and region are unified:
/// OCTOCON_SCYLLA_KEYSPACE controls both the Scylla session default keyspace and the
/// instance's assigned region for new-account creation and query routing.
/// </summary>
public sealed class PersistenceConfiguration
{
    public const string SectionName = "Octocon:Persistence";

    /// <summary>
    /// Persistence backend mode: 'scylla-postgres' or 'inmemory'.
    /// Default: 'scylla-postgres'
    /// </summary>
    public string Mode { get; set; } = "scylla-postgres";

    /// <summary>
    /// Instance region/keyspace identity. Controls both the Scylla session default
    /// keyspace and the region used for new-account creation and query routing.
    /// Values: nam, eur, ocn, eas, sam, sas, gdpr
    /// Default: 'nam'
    /// Env: OCTOCON_SCYLLA_KEYSPACE
    /// </summary>
    public string ScyllaKeyspace { get; set; } = "nam";

    /// <summary>
    /// PostgreSQL connection string.
    /// Env: OCTOCON_POSTGRES_CONNECTION
    /// </summary>
    public string PostgresConnectionString { get; set; } = "";

    /// <summary>
    /// When true, the migration service only creates the single keyspace specified by
    /// ScyllaKeyspace instead of all regional keyspaces. Useful for dev/single-instance setups.
    /// Default: false
    /// Env: OCTOCON_SINGLE_SCYLLA_INSTANCE
    /// </summary>
    public bool IsSingleScyllaInstance { get; set; } = false;

    /// <summary>
    /// Maximum number of retry attempts for transient database failures.
    /// Default: 3
    /// Env: OCTOCON_DB_RETRY_ATTEMPTS
    /// </summary>
    public int DbRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Initial backoff delay in milliseconds for exponential retry strategy.
    /// Default: 100
    /// Env: OCTOCON_DB_RETRY_INITIAL_DELAY_MS
    /// </summary>
    public int DbRetryInitialDelayMs { get; set; } = 100;

    /// <summary>
    /// Maximum backoff delay in milliseconds for exponential retry strategy.
    /// Default: 1500
    /// Env: OCTOCON_DB_RETRY_MAX_DELAY_MS
    /// </summary>
    public int DbRetryMaxDelayMs { get; set; } = 1500;

    /// <summary>
    /// Maximum concurrent friendship profile/fronting hydration tasks per request.
    /// Used to cap fan-out in friendship query paths to avoid unbounded bursts.
    /// Default: 8
    /// Env: OCTOCON_HYDRATION_MAX_CONCURRENCY
    /// </summary>
    public int HydrationMaxConcurrency { get; set; } = 8;
}
