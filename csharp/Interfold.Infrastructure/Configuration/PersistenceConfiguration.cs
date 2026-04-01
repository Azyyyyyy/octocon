namespace Interfold.Infrastructure.Configuration;

/// <summary>
/// Database and persistence configuration for Scylla, PostgreSQL, and retry logic.
/// Binds from environment variables with OCTOCON_ prefix:
///   - OCTOCON_PERSISTENCE (scylla-postgres or inmemory)
///   - OCTOCON_REGION (default: nam)
///   - OCTOCON_POSTGRES_CONNECTION
///   - OCTOCON_SCYLLA_* (keyspace, datacenter, contact points, credentials)
///   - OCTOCON_DB_RETRY_* (retry strategy parameters)
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
}
