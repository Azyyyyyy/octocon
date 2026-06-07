using System.Reflection;
using System.Text.RegularExpressions;
using Cassandra;
using Interfold.Contracts.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Interfold.Infrastructure.Scylla;

/// <summary>
/// Applies embedded CQL migrations at startup using admin credentials.
/// Creates keyspaces for all regions, applies schema, and grants DML permissions to the app user.
/// Supports both single-node (SimpleStrategy) and multi-DC (NetworkTopologyStrategy) deployments.
/// Runs before the app accepts traffic (IHostedLifecycleService.StartingAsync).
/// </summary>
public sealed partial class ScyllaMigrationService(
    PersistenceConfiguration options,
    ILogger<ScyllaMigrationService> logger) : IHostedLifecycleService
{
    private static readonly string[] RegionalKeyspaces = ["nam", "eur", "sam", "sas", "eas", "ocn", "gdpr"];

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ScyllaAdminUsername) ||
            string.IsNullOrWhiteSpace(options.ScyllaAdminPassword))
        {
            logger.LogInformation("[scylla-migrate] No admin credentials configured — skipping migrations.");
            return;
        }

        logger.LogInformation("[scylla-migrate] Applying ScyllaDB schema migrations...");

        var cluster = Cluster.Builder()
            .AddContactPoints(options.ScyllaContactPoints)
            .WithLoadBalancingPolicy(new DCAwareRoundRobinPolicy(options.ScyllaLocalDatacenter))
            .WithCredentials(options.ScyllaAdminUsername, options.ScyllaAdminPassword)
            .WithQueryTimeout(30000)
            .WithSocketOptions(new SocketOptions()
                .SetConnectTimeoutMillis(15000)
                .SetKeepAlive(true))
            .Build();

        try
        {
            var session = await cluster.ConnectAsync();

            try
            {
                var visibleDcs = await DiscoverDatacenters(session);
                logger.LogInformation("[scylla-migrate] Visible datacenters: {DCs}", string.Join(", ", visibleDcs));

                await ApplyKeyspaces(session, visibleDcs);
                await ApplySchema(session);
                await GrantPermissions(session);
            }
            finally
            {
                session.Dispose();
            }
        }
        finally
        {
            await cluster.ShutdownAsync();
        }

        logger.LogInformation("[scylla-migrate] All migrations applied.");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // --- DC Discovery ---

    private async Task<HashSet<string>> DiscoverDatacenters(ISession session)
    {
        var dcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var localRows = await session.ExecuteAsync(new SimpleStatement("SELECT data_center FROM system.local"));
        foreach (var row in localRows)
        {
            var dc = row.GetValue<string>("data_center");
            if (!string.IsNullOrWhiteSpace(dc)) dcs.Add(dc.ToLowerInvariant());
        }

        var peerRows = await session.ExecuteAsync(new SimpleStatement("SELECT data_center FROM system.peers"));
        foreach (var row in peerRows)
        {
            var dc = row.GetValue<string>("data_center");
            if (!string.IsNullOrWhiteSpace(dc)) dcs.Add(dc.ToLowerInvariant());
        }

        // Only keep known regional DCs
        dcs.IntersectWith(RegionalKeyspaces);
        return dcs;
    }

    // --- Replication Strategies (mirrors scylla-load-keyspaces.sh) ---

    private static bool IsMultiDc(HashSet<string> dcs) => dcs.Count > 1;

    private static string SimpleReplication() =>
        "{'class': 'SimpleStrategy', 'replication_factor': '1'}";

    private static string NtsReplication(HashSet<string> visibleDcs, params string[] preferredDcs)
    {
        // Only include DCs that actually exist in the cluster
        var actualDcs = preferredDcs.Where(visibleDcs.Contains).ToArray();
        if (actualDcs.Length == 0) actualDcs = [visibleDcs.First()];
        var pairs = string.Join(", ", actualDcs.Select(dc => $"'{dc}': '1'"));
        return $"{{'class': 'NetworkTopologyStrategy', {pairs}}}";
    }

    private static string RegionalReplicationFor(string keyspace, HashSet<string> dcs)
    {
        if (!IsMultiDc(dcs)) return SimpleReplication();

        return keyspace switch
        {
            "nam" => NtsReplication(dcs, "nam", "eur", "sam"),
            "eur" => dcs.Contains("eur") ? NtsReplication(dcs, "nam", "eur", "sas") : NtsReplication(dcs, "nam", "sam", "sas"),
            "sam" => NtsReplication(dcs, "nam", "sam", "eas"),
            "sas" => dcs.Contains("sas") ? NtsReplication(dcs, "nam", "sas", "ocn") : NtsReplication(dcs, "nam", "sas", "gdpr"),
            "eas" => dcs.Contains("eas") ? NtsReplication(dcs, "nam", "eas", "ocn") : NtsReplication(dcs, "nam", "eas", "gdpr"),
            "ocn" => dcs.Contains("ocn") ? NtsReplication(dcs, "nam", "ocn", "gdpr") : NtsReplication(dcs, "nam", "ocn", "sas"),
            "gdpr" => dcs.Contains("gdpr") ? NtsReplication(dcs, "nam", "gdpr", "eur") : NtsReplication(dcs, "nam", "eur", "ocn"),
            _ => NtsReplication(dcs, "nam", "eur", "sam")
        };
    }

    private static string GlobalReplication(HashSet<string> dcs)
    {
        if (!IsMultiDc(dcs)) return SimpleReplication();

        // Global keyspace replicates to all available DCs
        var allDcs = new[] { "nam", "eur", "sam", "sas", "eas", "ocn", "gdpr" }
            .Where(dcs.Contains)
            .ToArray();
        return NtsReplication(dcs, allDcs);
    }

    private static string NamNtReplication(HashSet<string> dcs)
    {
        if (!IsMultiDc(dcs)) return SimpleReplication();
        return NtsReplication(dcs, "nam");
    }

    // --- Keyspace Creation ---

    private async Task ApplyKeyspaces(ISession session, HashSet<string> dcs)
    {
        var cqlTemplate = GetEmbeddedResource("001_create_octocon_keyspaces.cql");
        var globalRepl = GlobalReplication(dcs);
        var namNtRepl = NamNtReplication(dcs);

        var keyspaces = options.ScyllaSingleKeyspace
            ? [options.ScyllaKeyspace]
            : RegionalKeyspaces;

        // Create keyspaces for target regions
        foreach (var keyspace in keyspaces)
        {
            var regionalRepl = RegionalReplicationFor(keyspace, dcs);
            var tabletsEnabled = IsMultiDc(dcs) ? "true" : "false";
            var rendered = cqlTemplate
                .Replace("{{KEYSPACE}}", keyspace)
                .Replace("{{KEYSPACE_REPLICATION}}", regionalRepl)
                .Replace("{{GLOBAL_REPLICATION}}", globalRepl)
                .Replace("{{NAM_NT_REPLICATION}}", namNtRepl)
                .Replace("{{TABLETS_ENABLED}}", tabletsEnabled);

            logger.LogInformation("[scylla-migrate] Creating keyspaces for region '{Region}' (replication={Repl})...",
                keyspace, regionalRepl);
            await ExecuteStatements(session, rendered);
        }
    }

    // --- Schema Application ---

    private async Task ApplySchema(ISession session)
    {
        var cqlTemplate = GetEmbeddedResource("002_create_octocon_schema.templated.cql");

        var keyspaces = options.ScyllaSingleKeyspace
            ? [options.ScyllaKeyspace]
            : RegionalKeyspaces;

        foreach (var keyspace in keyspaces)
        {
            var rendered = cqlTemplate.Replace("{{KEYSPACE}}", keyspace);
            logger.LogInformation("[scylla-migrate] Applying schema to keyspace '{Keyspace}'...", keyspace);
            await ExecuteStatements(session, rendered);
        }
    }

    // --- Permission Grants ---

    private async Task GrantPermissions(ISession session)
    {
        var appUser = options.ScyllaUsername;
        if (string.IsNullOrWhiteSpace(appUser))
        {
            logger.LogWarning("[scylla-migrate] No app user configured — skipping permission grants.");
            return;
        }

        logger.LogInformation("[scylla-migrate] Granting DML permissions to '{AppUser}'...", appUser);

        var keyspaces = options.ScyllaSingleKeyspace
            ? [options.ScyllaKeyspace]
            : RegionalKeyspaces;

        // Grant on regional keyspaces
        foreach (var keyspace in keyspaces)
        {
            await GrantKeyspacePermissions(session, keyspace, appUser);
        }

        // Grant on fixed keyspaces (global, nam_nt, dummy)
        await GrantKeyspacePermissions(session, "global", appUser);
        await GrantKeyspacePermissions(session, "nam_nt", appUser);
        await GrantKeyspacePermissions(session, "dummy", appUser);
    }

    private async Task GrantKeyspacePermissions(ISession session, string keyspace, string user)
    {
        var grants = new[]
        {
            $"GRANT SELECT ON KEYSPACE {keyspace} TO '{user}'",
            $"GRANT MODIFY ON KEYSPACE {keyspace} TO '{user}'"
        };

        foreach (var stmt in grants)
        {
            try
            {
                await session.ExecuteAsync(new SimpleStatement(stmt));
            }
            catch (InvalidQueryException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // Permission already granted — idempotent
            }
        }
    }

    // --- CQL Execution ---

    private async Task ExecuteStatements(ISession session, string cql)
    {
        var statements = SplitCqlStatements(cql);
        foreach (var stmt in statements)
        {
            logger.LogDebug("[scylla-migrate] Executing: {Stmt}", stmt[..Math.Min(stmt.Length, 80)]);
            try
            {
                await session.ExecuteAsync(new SimpleStatement(stmt));
            }
            catch (AlreadyExistsException)
            {
                // Idempotent — keyspace/table/type/index already exists
            }
        }
    }

    private static List<string> SplitCqlStatements(string cql)
    {
        return StatementSplitter().Split(cql)
            .Select(StripCommentLines)
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static string StripCommentLines(string chunk)
    {
        var lines = chunk.Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal) && l.Length > 0);
        return string.Join('\n', lines).Trim();
    }

    [GeneratedRegex(@";\s*$", RegexOptions.Multiline)]
    private static partial Regex StatementSplitter();

    private static string GetEmbeddedResource(string filename)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Interfold.Infrastructure.Scylla.Migrations.{filename}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
