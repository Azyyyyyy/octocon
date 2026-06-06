namespace Interfold.Contracts.Configuration;

/// <summary>
/// Database and persistence configuration for Scylla, PostgreSQL, and retry logic.
/// Binds from environment variables with OCTOCON_ prefix:
///   - OCTOCON_PERSISTENCE (scylla-postgres or inmemory)
///   - OCTOCON_REGION (default: nam)
///   - OCTOCON_POSTGRES_CONNECTION
///   - OCTOCON_POSTGRES_ADMIN_CONNECTION (admin for schema migrations)
///   - OCTOCON_SCYLLA_* (keyspace, datacenter, contact points, credentials)
///   - OCTOCON_SCYLLA_ADMIN_USERNAME / OCTOCON_SCYLLA_ADMIN_PASSWORD
///   - OCTOCON_DB_RETRY_* (retry strategy parameters)
///   - OCTOCON_HYDRATION_MAX_CONCURRENCY
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
    /// Default region for data locality and keyspace selection.
    /// Values: nam, eur, ocn, eas, sam, sas, gdpr
    /// Default: 'nam'
    /// </summary>
    public string DefaultRegion { get; set; } = "nam";

    /// <summary>
    /// PostgreSQL connection string.
    /// Default: 'Host=localhost;Port=5432;Database=octocon;Username=octocon;Password=octocon'
    /// Env: OCTOCON_POSTGRES_CONNECTION
    /// </summary>
    public string PostgresConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=octocon;Username=octocon;Password=octocon";

    /// <summary>
    /// PostgreSQL admin connection string used for schema migrations at startup.
    /// If null/empty, migrations are skipped.
    /// Env: OCTOCON_POSTGRES_ADMIN_CONNECTION
    /// </summary>
    public string? PostgresAdminConnectionString { get; set; }

    /// <summary>
    /// Scylla admin username for schema migrations at startup.
    /// If null/empty, migrations are skipped.
    /// Env: OCTOCON_SCYLLA_ADMIN_USERNAME
    /// </summary>
    public string? ScyllaAdminUsername { get; set; }

    /// <summary>
    /// Scylla admin password (paired with ScyllaAdminUsername).
    /// Env: OCTOCON_SCYLLA_ADMIN_PASSWORD
    /// </summary>
    public string? ScyllaAdminPassword { get; set; }

    /// <summary>
    /// When true, the migration service only creates the single keyspace specified by
    /// ScyllaKeyspace instead of all regional keyspaces. Useful for dev/single-instance setups.
    /// Default: false
    /// Env: OCTOCON_SCYLLA_SINGLE_KEYSPACE
    /// </summary>
    public bool ScyllaSingleKeyspace { get; set; } = false;

    /// <summary>
    /// Compatibility mode for Scylla-only operation in 'scylla-postgres' mode.
    /// When true, idempotency and auth token revocation use in-memory stores and
    /// Postgres bootstrap checks are skipped.
    /// Default: false
    /// Env: OCTOCON_COMPATIBILITY_MODE
    /// </summary>
    public bool CompatibilityMode { get; set; } = false;

    /// <summary>
    /// Scylla default keyspace (typically matches region).
    /// Default: 'nam' (matches DefaultRegion)
    /// Env: OCTOCON_SCYLLA_KEYSPACE
    /// </summary>
    public string ScyllaKeyspace { get; set; } = "nam";

    /// <summary>
    /// Scylla local datacenter name for driver locality awareness.
    /// Default: 'datacenter1'
    /// Env: OCTOCON_SCYLLA_DATACENTER
    /// </summary>
    public string ScyllaLocalDatacenter { get; set; } = "datacenter1";

    /// <summary>
    /// Scylla contact points (comma-separated, parsed from OCTOCON_SCYLLA_CONTACT_POINTS).
    /// Default: ['127.0.0.1']
    /// Env: OCTOCON_SCYLLA_CONTACT_POINTS (comma/space separated)
    /// </summary>
    public string[] ScyllaContactPoints { get; set; } = ["127.0.0.1"];

    /// <summary>
    /// Scylla authentication username (null = no auth).
    /// Env: OCTOCON_SCYLLA_USERNAME
    /// </summary>
    public string? ScyllaUsername { get; set; }

    /// <summary>
    /// Scylla authentication password (paired with ScyllaUsername).
    /// Env: OCTOCON_SCYLLA_PASSWORD
    /// </summary>
    public string? ScyllaPassword { get; set; }

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
