namespace Interfold.Infrastructure.Persistence;

public sealed class PersistenceRegistrationOptions
{
    public string DefaultRegion { get; set; } = "nam";

    public string PostgresConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=octocon;Username=octocon;Password=octocon";

    public string[] ScyllaContactPoints { get; set; } = ["127.0.0.1"];
    public string ScyllaLocalDatacenter { get; set; } = "datacenter1";
    // Canonical strategy is keyspace-per-region, so default to the default region keyspace.
    public string ScyllaKeyspace { get; set; } = "nam";
    public string? ScyllaUsername { get; set; }
    public string? ScyllaPassword { get; set; }

    public int DbRetryAttempts { get; set; } = 3;
    public int DbRetryInitialDelayMs { get; set; } = 100;
    public int DbRetryMaxDelayMs { get; set; } = 1500;
}