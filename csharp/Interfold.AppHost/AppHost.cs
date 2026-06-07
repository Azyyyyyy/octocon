using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Net.Sockets;

var builder = DistributedApplication.CreateBuilder(args);

// --- Health checks for the Aspire dashboard ---
builder.Services.AddHealthChecks()
    .AddCheck("msg-db-health", () =>
    {
        try
        {
            using var tcp = new TcpClient();
            tcp.Connect("localhost", 4200);
            return HealthCheckResult.Healthy();
        }
        catch { return HealthCheckResult.Unhealthy(); }
    })
    .AddCheck("scylla-health", () =>
    {
        try
        {
            using var tcp = new TcpClient();
            tcp.Connect("localhost", 9042);
            return HealthCheckResult.Healthy();
        }
        catch { return HealthCheckResult.Unhealthy(); }
    });

// --- Docker Compose publish target with network isolation ---
builder.AddDockerComposeEnvironment("docker-compose")
    .ConfigureComposeFile(compose =>
    {
        compose.AddNetwork(new Network { Name = "scylla", Driver = "bridge" });
        compose.AddNetwork(new Network { Name = "postgres", Driver = "bridge" });
        compose.AddNetwork(new Network { Name = "api", Driver = "bridge" });

        // Dashboard needs to be reachable by API for OTLP telemetry
        if (compose.Services.TryGetValue("docker-compose-dashboard", out var dashboard))
        {
            dashboard.Networks.Add("api");
        }
    });

// --- Parameters (set via user-secrets or environment variables) ---
var postgresUser = builder.AddParameter("postgres-user");
var postgresPassword = builder.AddParameter("postgres-password", secret: true);
var scyllaUser = builder.AddParameter("scylla-user");
var scyllaPassword = builder.AddParameter("scylla-password", secret: true);
var scyllaAdminPassword = builder.AddParameter("scylla-admin-password", secret: true);
var encryptionPrivateKey = builder.AddParameter("encryption-private-key", secret: true);
var googleOAuthClientSecret = builder.AddParameter("google-oauth-client-secret", secret: true);
var discordOAuthClientSecret = builder.AddParameter("discord-oauth-client-secret", secret: true);
var encryptionPepper = builder.AddParameter("encryption-pepper", secret: true);

// --- Reject well-known default passwords at startup (dev mode only; compose relies on shell guard) ---
if (!builder.ExecutionContext.IsPublishMode)
{
    var scyllaPasswordValue = builder.Configuration["Parameters:scylla-password"];
    if (string.IsNullOrEmpty(scyllaPasswordValue) || scyllaPasswordValue == "cassandra")
    {
        throw new InvalidOperationException(
            "SCYLLA_PASSWORD must be set to a non-default value. " +
            "The well-known default 'cassandra' is not allowed.\n" +
            "Fix: dotnet user-secrets set \"Parameters:scylla-password\" \"<your-secure-password>\" " +
            "--project csharp/Interfold.AppHost");
    }

    var scyllaUserValue = builder.Configuration["Parameters:scylla-user"];
    if (string.IsNullOrEmpty(scyllaUserValue) || scyllaUserValue == "cassandra")
    {
        throw new InvalidOperationException(
            "SCYLLA_USER must be set to a non-default value. " +
            "The well-known default 'cassandra' is not allowed.\n" +
            "Fix: dotnet user-secrets set \"Parameters:scylla-user\" \"<your-username>\" " +
            "--project csharp/Interfold.AppHost");
    }

    var postgresPasswordValue = builder.Configuration["Parameters:postgres-password"];
    if (string.IsNullOrEmpty(postgresPasswordValue) || postgresPasswordValue == "postgres")
    {
        throw new InvalidOperationException(
            "POSTGRES_PASSWORD must be set to a non-default value. " +
            "The well-known default 'postgres' is not allowed.\n" +
            "Fix: dotnet user-secrets set \"Parameters:postgres-password\" \"<your-secure-password>\" " +
            "--project csharp/Interfold.AppHost");
    }

    var postgresUserValue = builder.Configuration["Parameters:postgres-user"];
    if (string.IsNullOrEmpty(postgresUserValue) || postgresUserValue == "postgres")
    {
        throw new InvalidOperationException(
            "POSTGRES_USER must be set to a non-default value. " +
            "The well-known default 'postgres' is not allowed.\n" +
            "Fix: dotnet user-secrets set \"Parameters:postgres-user\" \"<your-username>\" " +
            "--project csharp/Interfold.AppHost");
    }
}

// --- Configurable host ports ---
int Port(string name, int fallback) => int.TryParse(builder.Configuration[$"Ports:{name}"], out var p) ? p : fallback;
var postgresPort = Port("postgres", 4200);
var scyllaPort = Port("scylla", 9042);
var apiHttpPort = Port("api-http", 5000);
var apiHttpsPort = Port("api-https", 5001);
var webHttpPort = Port("web-http", 8080);
var webHttpsPort = Port("web-https", 8081);

// --- PostgreSQL (TimescaleDB) ---
// The container starts with a disposable init superuser (pg_init). The bootstrap
// script then creates the real app user (non-superuser) and an admin account.
// We can't demote the cluster owner, so we use a separate init account.
var msgDb = builder.AddContainer("msg-db", "timescale/timescaledb", "latest-pg18")
    .WithContainerNetworkAlias("msg-db")
    .WithEnvironment("POSTGRES_USER", "db_init")
    .WithEnvironment("POSTGRES_PASSWORD", postgresPassword)
    .WithEnvironment("PGDATA", "/var/lib/postgresql/data/pgdata")
    .WithVolume("msg_pgdata", "/var/lib/postgresql/data")
    .WithEndpoint(port: postgresPort, targetPort: 5432, name: "postgres", scheme: "tcp")
    .WithHealthCheck("msg-db-health")
    .WithLifetime(ContainerLifetime.Persistent)
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Networks = ["postgres"];
        service.Healthcheck = new Healthcheck
        {
            Test = ["CMD-SHELL", "pg_isready -U $POSTGRES_USER -d postgres"],
            Interval = "10s",
            Timeout = "5s",
            Retries = 10,
            StartPeriod = "15s"
        };
    });

// --- ScyllaDB / Cassandra (conditional on launch profile) ---
IResourceBuilder<ContainerResource>? scyllaEndpointOwner = null;
string loadKeysImage;
string loadKeysTag;
string scyllaFirstHost;
IResourceBuilder<ContainerResource> loadKeysWaitTarget;

var mode = builder.Configuration["Parameters:scylla-mode"] ?? "single";

if (mode == "cassandra")
{
    // --- Cassandra 4.1 (low-end fallback) ---
    var cassandra = builder.AddContainer("cassandra", "cassandra", "5")
        .WithContainerNetworkAlias("cassandra")
        .WithEnvironment("CASSANDRA_CLUSTER_NAME", "OctoCluster")
        .WithEnvironment("CASSANDRA_LISTEN_ADDRESS", "cassandra")
        .WithEnvironment("CASSANDRA_BROADCAST_ADDRESS", "cassandra")
        .WithEnvironment("CASSANDRA_BROADCAST_RPC_ADDRESS", "cassandra")
        .WithEnvironment("CASSANDRA_RPC_ADDRESS", "0.0.0.0")
        .WithEnvironment("CASSANDRA_ENDPOINT_SNITCH", "GossipingPropertyFileSnitch")
        .WithEnvironment("CASSANDRA_NUM_TOKENS", "16")
        .WithEnvironment("MAX_HEAP_SIZE", "512M")
        .WithEnvironment("HEAP_NEWSIZE", "256M")
        .WithEnvironment("CQLSH_USER", scyllaUser)
        .WithEnvironment("CQLSH_PASSWORD", scyllaPassword)
        .WithBindMount("../../scripts/cassandra/enable-mv.sh", "/enable-mv.sh", isReadOnly: true)
        .WithBindMount("../../db/scylla/cassandra-rackdc.nam.properties", "/etc/cassandra/cassandra-rackdc.properties", isReadOnly: true)
        .WithVolume("cassandra_data", "/var/lib/cassandra")
        .WithEndpoint(port: scyllaPort, targetPort: 9042, name: "cql", scheme: "tcp")
        .WithHealthCheck("scylla-health")
        .WithEntrypoint("/enable-mv.sh")
        .WithLifetime(ContainerLifetime.Persistent)
        .PublishAsDockerComposeService((_, service) =>
        {
            service.Networks = ["scylla"];
            service.Healthcheck = new Healthcheck
            {
                Test = ["CMD-SHELL", "cqlsh -u $CQLSH_USER -p $CQLSH_PASSWORD -e 'describe cluster' || nodetool status | grep -q '^UN'"],
                Interval = "15s",
                Timeout = "10s",
                Retries = 20,
                StartPeriod = "30s"
            };
        });

    loadKeysImage = "cassandra";
    loadKeysTag = "5";
    loadKeysWaitTarget = cassandra;
    scyllaEndpointOwner = cassandra;
    scyllaFirstHost = "cassandra";
}
else
{
    // --- ScyllaDB nodes (single or multi based on mode) ---
    string[] regions = mode == "multi"
        ? ["nam", "eur", "sam", "sas", "eas", "ocn", "gdpr"]
        : ["nam"];

    IResourceBuilder<ContainerResource>? previousNode = null;

    foreach (var region in regions)
    {
        var name = regions.Length > 1 ? $"scylla-{region}" : "scylla";
        var seeds = previousNode is null ? $"--seeds={name}" : $"--seeds=scylla-{regions[0]},scylla-{regions[1]}";

        var node = builder.AddContainer(name, "scylladb/scylla", "2026.1")
            .WithContainerNetworkAlias(name)
            .WithArgs(seeds, "--smp", "1", "--memory", "750M", "--overprovisioned", "1",
                "--developer-mode", "1", "--authenticator", "PasswordAuthenticator",
                "--authorizer", "CassandraAuthorizer",
                "--endpoint-snitch", "GossipingPropertyFileSnitch",
                "--api-address", "0.0.0.0", "--broadcast-address", name)
            .WithBindMount($"../../db/scylla/cassandra-rackdc.{region}.properties", "/etc/scylla/cassandra-rackdc.properties", isReadOnly: true)
            .WithVolume(regions.Length > 1 ? $"scylla_{region}_data" : "scylla_data", "/var/lib/scylla")
            .WithLifetime(ContainerLifetime.Persistent)
            .WithEnvironment("CQLSH_USER", scyllaUser)
            .WithEnvironment("CQLSH_PASSWORD", scyllaPassword)
            .PublishAsDockerComposeService((_, service) =>
            {
                service.Networks = ["scylla"];
                service.Healthcheck = new Healthcheck
                {
                    Test = ["CMD-SHELL", "nodetool status | grep -q '^UN'"],
                    Interval = "15s",
                    Timeout = "10s",
                    Retries = 20,
                    StartPeriod = "30s"
                };
            });

        if (previousNode is null)
        {
            node.WithEndpoint(port: scyllaPort, targetPort: 9042, name: "cql", scheme: "tcp");
            node.WithHealthCheck("scylla-health");
            scyllaEndpointOwner = node;
        }
        else
            node.WaitFor(previousNode);

        previousNode = node;
    }

    var firstNode = regions.Length > 1 ? $"scylla-{regions[0]}" : "scylla";

    loadKeysImage = "scylladb/scylla";
    loadKeysTag = "2026.1";
    loadKeysWaitTarget = previousNode!;
    scyllaFirstHost = firstNode;
}

// --- CQL Auth Bootstrap (creates admin superuser, app user as non-superuser, locks cassandra) ---
var scyllaBootstrapAuth = builder.AddContainer("scylla-bootstrap-auth", loadKeysImage, loadKeysTag)
    .WithBindMount("../../scripts/scylla/scylla-bootstrap-auth.sh", "/scripts/scylla-bootstrap-auth.sh", isReadOnly: true)
    .WithEnvironment("SCYLLA_USER", scyllaUser)
    .WithEnvironment("SCYLLA_PASSWORD", scyllaPassword)
    .WithEnvironment("SCYLLA_ADMIN_PASSWORD", scyllaAdminPassword)
    .WithEntrypoint("/bin/bash")
    .WithArgs("/scripts/scylla-bootstrap-auth.sh", scyllaFirstHost)
    .WaitFor(loadKeysWaitTarget)
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Networks = ["scylla"];
        foreach (var dep in service.DependsOn.Values)
        {
            if (dep.Condition == "service_started")
                dep.Condition = "service_healthy";
        }
    });

// --- Endpoint references (resolve to localhost:{hostPort} in dev, container:{targetPort} in compose) ---
var pgEndpoint = msgDb.GetEndpoint("postgres");

// --- PostgreSQL Auth Bootstrap (creates app user, admin account, seeds secrets) ---
var pgBootstrapAuth = builder.AddContainer("pg-bootstrap-auth", "timescale/timescaledb", "latest-pg18")
    .WithBindMount("../../scripts/postgres/postgres-bootstrap-auth.sh", "/scripts/postgres-bootstrap-auth.sh", isReadOnly: true)
    .WithBindMount("../../csharp/Interfold.Infrastructure.Postgres/Migrations/000_create_secrets_table.sql", "/scripts/000_create_secrets_table.sql", isReadOnly: true)
    .WithEnvironment("PG_INIT_PASSWORD", postgresPassword)
    .WithEnvironment("PGUSER", postgresUser)
    .WithEnvironment("PGPASSWORD", postgresPassword)
    .WithEnvironment("SCYLLA_USER", scyllaUser)
    .WithEnvironment("SCYLLA_PASSWORD", scyllaPassword)
    .WithEnvironment("SCYLLA_ADMIN_PASSWORD", scyllaAdminPassword)
    .WithEnvironment("SCYLLA_CONTACT_POINTS",
        builder.ExecutionContext.IsPublishMode ? scyllaFirstHost : "localhost")
    .WithEnvironment("SCYLLA_DATACENTER", "nam")
    .WithEnvironment("SCYLLA_KEYSPACE", "nam")
    .WithEnvironment("OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET", googleOAuthClientSecret)
    .WithEnvironment("OCTOCON_DISCORD_OAUTH_CLIENT_SECRET", discordOAuthClientSecret)
    .WithEnvironment("OCTOCON_ENCRYPTION_PEPPER", encryptionPepper)
    .WithEntrypoint("/bin/bash")
    .WithArgs("/scripts/postgres-bootstrap-auth.sh", "msg-db", "octocon")
    .WaitFor(msgDb)
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Networks = ["postgres"];
        if (service.DependsOn.TryGetValue("msg-db", out var dep))
            dep.Condition = "service_healthy";
    });

// --- Interfold API (from source) ---
var api = builder.AddProject<Projects.Interfold_Api>("interfold-api")
    .WithHttpEndpoint(port: apiHttpPort, targetPort: 5100, name: "http")
    .WithHttpsEndpoint(port: apiHttpsPort, targetPort: 5101, name: "https")
    .WithHttpHealthCheck("/health/ready", endpointName: "http")
    .WithEnvironment("OCTOCON_PERSISTENCE", "scylla-postgres")
    .WithEnvironment("OCTOCON_SINGLE_SCYLLA_INSTANCE", mode == "multi" ? "false" : "true")
    .WithEnvironment("OCTOCON_POSTGRES_CONNECTION",
        ReferenceExpression.Create($"Host={pgEndpoint.Property(EndpointProperty.Host)};Port={pgEndpoint.Property(EndpointProperty.Port)};Database=octocon;Username={postgresUser};Password={postgresPassword}"))
    .WithEnvironment("ENCRYPTION_PRIVATE_KEY", encryptionPrivateKey)
    .WithExternalHttpEndpoints()
    .WaitForCompletion(pgBootstrapAuth)
    .WaitForCompletion(scyllaBootstrapAuth)
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Networks = ["scylla", "postgres", "api"];
        service.Healthcheck = new Healthcheck
        {
            Test = ["CMD", "curl", "-f", "http://localhost:5100/health/ready"],
            Interval = "15s",
            Timeout = "5s",
            Retries = 10,
            StartPeriod = "20s"
        };
    });

// --- Octocon Web (pre-built container, optional) ---
var includeWeb = !string.Equals(builder.Configuration["Parameters:include-web"], "false", StringComparison.OrdinalIgnoreCase);
if (includeWeb)
{
    builder.AddContainer("octocon-web", "ghcr.io/azyyyyyy/octocon-wasm", "latest")
        .WithContainerNetworkAlias("octocon-web")
        .WithHttpEndpoint(port: webHttpPort, targetPort: 80, name: "http")
        .WithHttpEndpoint(port: webHttpsPort, targetPort: 8080, name: "https")
        .WithHttpHealthCheck("/", endpointName: "http")
        .WithExternalHttpEndpoints()
        .PublishAsDockerComposeService((_, service) =>
        {
            service.Networks = ["api"];
            service.Healthcheck = new Healthcheck
            {
                Test = ["CMD", "curl", "-f", "http://localhost:80/"],
                Interval = "15s",
                Timeout = "5s",
                Retries = 5,
                StartPeriod = "10s"
            };
        });
}

builder.Build().Run();
