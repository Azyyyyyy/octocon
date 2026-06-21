using System.Text.Json.Serialization;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>
/// Operator-supplied configuration loaded from <c>interfold.bootstrap.json</c> or built via
/// interactive prompts. Holds everything that <em>cannot</em> be generated automatically (domains,
/// OAuth secrets, port numbers, etc.).
/// </summary>
public sealed class BootstrapConfig
{
    [JsonPropertyName("deployment")]
    public DeploymentSection Deployment { get; set; } = new();

    [JsonPropertyName("ports")]
    public PortsSection Ports { get; set; } = new();

    /// <summary>
    /// Database stack to deploy: <c>single</c> (one Scylla node), <c>multi</c> (7-region
    /// regional Scylla cluster), or <c>cassandra</c> (single Cassandra 5 node, no Scylla).
    /// PublishPhase translates this enum into the orthogonal AppHost parameters
    /// <c>include-scylla</c> / <c>include-cassandra</c> / <c>scylla-topology</c>.
    /// </summary>
    [JsonPropertyName("databaseMode")]
    public string DatabaseMode { get; set; } = "single";

    /// <summary>
    /// Pre-built Interfold API container image reference. The bootstrapper does NOT build the API
    /// from source - operators are expected to publish the API image to a registry and reference it
    /// here, then the generated docker-compose pulls/uses that image directly. Defaults to the
    /// canonical GHCR tag but can be overridden for testing or private registries.
    /// </summary>
    [JsonPropertyName("apiImage")]
    public string ApiImage { get; set; } = "ghcr.io/interfold/api:latest";

    /// <summary>
    /// Name of the Postgres application database that <see cref="Phases.DatabaseInitPhase"/> creates
    /// and the API connects to. Defaults to <c>interfold</c>. Operators on a shared cluster (or who
    /// want a brand-specific name) can set this to any safe Postgres identifier; the value is
    /// validated by <see cref="Phases.ConfigPhase.Validate"/>. The seeder injects it into
    /// <c>CREATE DATABASE "{value}"</c> via <see cref="DatabaseBootstrap.PostgresSqlTemplates"/>, so
    /// it must satisfy Postgres' 63-byte NAMEDATALEN budget and contain only
    /// <c>[A-Za-z_][A-Za-z0-9_]*</c>.
    /// </summary>
    [JsonPropertyName("postgresDatabase")]
    public string PostgresDatabase { get; set; } = "interfold";

    /// <summary>
    /// Cluster identity advertised by both the ScyllaDB and Cassandra backends. Defaults to
    /// <c>InterfoldCluster</c>. The value lands on Cassandra's <c>CASSANDRA_CLUSTER_NAME</c> env
    /// var (read by the entrypoint into <c>cassandra.yaml</c>) and on Scylla's
    /// <c>--cluster-name</c> CLI flag, and is what <c>SELECT cluster_name FROM system.local</c>
    /// returns. Pure metadata: it does not appear in any CQL the API issues, and there is no
    /// keyspace or table naming contract attached to it. Validated by
    /// <see cref="Phases.ConfigPhase.Validate"/> to reject empty values and characters that
    /// would break either Scylla's CLI argument parsing or Cassandra's
    /// <c>cassandra.yaml</c> rewrite (single quotes, newlines, control chars).
    /// </summary>
    [JsonPropertyName("clusterName")]
    public string ClusterName { get; set; } = "InterfoldCluster";

    [JsonPropertyName("oauth")]
    public OAuthSection OAuth { get; set; } = new();
}

public sealed class DeploymentSection
{
    [JsonPropertyName("outputDir")]
    public string OutputDir { get; set; } = "./deploy";

    [JsonPropertyName("domains")]
    public List<string> Domains { get; set; } = ["api.example.com"];

    [JsonPropertyName("rootCaName")]
    public string RootCaName { get; set; } = "Interfold Root CA";

    [JsonPropertyName("certYears")]
    public int CertYears { get; set; } = 5;

    [JsonPropertyName("trustStoreInstall")]
    public bool TrustStoreInstall { get; set; } = true;

    /// <summary>
    /// Opt-in HTTPS termination for the <c>octocon-web</c> container. When <c>true</c>, the
    /// generated compose:
    /// <list type="bullet">
    ///   <item>Includes the <c>octocon-web</c> service unconditionally (overrides the default-off
    ///         <c>Parameters:include-web</c>).</item>
    ///   <item>Bind-mounts the bootstrapper-issued <c>certs/</c> directory into the container at
    ///         <c>/certs</c> (read-only) so nginx can read <c>leaf.crt</c> and <c>leaf.key</c>.</item>
    ///   <item>Bind-mounts a generated nginx template into
    ///         <c>/etc/nginx/templates/default.conf.template</c> so the official
    ///         <c>nginx</c> image's envsubst step renders the TLS server block at startup.</item>
    /// </list>
    /// Defaults to <c>false</c> to preserve the existing HTTP-only behaviour for operators
    /// that don't want TLS terminated at the web tier (e.g. those fronting compose with an
    /// external load balancer that handles TLS).
    /// </summary>
    [JsonPropertyName("webHttps")]
    public bool WebHttps { get; set; } = false;
}

public sealed class PortsSection
{
    [JsonPropertyName("apiHttp")]
    public int ApiHttp { get; set; } = 5000;

    [JsonPropertyName("apiHttps")]
    public int ApiHttps { get; set; } = 5001;

    [JsonPropertyName("webHttp")]
    public int WebHttp { get; set; } = 8080;

    [JsonPropertyName("webHttps")]
    public int WebHttps { get; set; } = 8081;

    /// <summary>
    /// Host port the Postgres / TimescaleDB container binds (compose-side). The AppHost graph
    /// reads this from <c>Ports:postgres</c> and the matching connection-string that the API
    /// consumes is derived from it. Defaults to 4200 to match the upstream AppHost configuration
    /// and keep existing operator configs working unchanged. Tests can override the value to
    /// run multiple compose stacks in parallel inside the same Docker host.
    /// </summary>
    [JsonPropertyName("postgres")]
    public int Postgres { get; set; } = 4200;

    /// <summary>
    /// Host port the Scylla / Cassandra container binds (compose-side). The AppHost graph reads
    /// this from <c>Ports:scylla</c> for both Scylla and the lone-Cassandra fallback (see
    /// <c>InterfoldAppHost.Configure</c>). Defaults to 9042 to match the upstream AppHost and
    /// the long-standing CQL convention; tests can override it for parallel compose stacks.
    /// </summary>
    [JsonPropertyName("scylla")]
    public int Scylla { get; set; } = 9042;
}

public sealed class OAuthSection
{
    [JsonPropertyName("googleClientSecret")]
    public string GoogleClientSecret { get; set; } = string.Empty;

    [JsonPropertyName("discordClientSecret")]
    public string DiscordClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Apple Sign-In client secret. In Apple's flow this is a short-lived JWT derived from
    /// the team's private key — operators that pre-mint one outside the bootstrapper paste
    /// it here. Leave empty to skip the seed row (matches the Google/Discord pattern).
    /// </summary>
    [JsonPropertyName("appleClientSecret")]
    public string AppleClientSecret { get; set; } = string.Empty;
}
