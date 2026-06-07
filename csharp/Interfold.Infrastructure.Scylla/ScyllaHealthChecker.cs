using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Interfold.Infrastructure.Scylla;

public class ScyllaHealthChecker : IHealthCheck
{
    private const string GlobalKeyspace = "global";

    private static readonly string[] RequiredGlobalScyllaTables =
    [
        "user_registry",
        "notification_tokens",
        "friendships",
        "friend_requests"
    ];

    private static readonly string[] RequiredRegionalScyllaTables =
    [
        "users",
        "alters",
        "tags",
        "alter_tags",
        "polls",
        "fronts",
        "current_fronts",
        "global_journals",
        "global_journal_alters",
        "alter_journals"
    ];

    private readonly IScyllaSessionProvider _scyllaSessionProvider;
    private readonly PersistenceConfiguration _options;
    private readonly ISecretsStore _secretsStore;
    private readonly IConfiguration _configuration;

    public ScyllaHealthChecker(
        IScyllaSessionProvider scyllaSessionProvider,
        PersistenceConfiguration options,
        ISecretsStore secretsStore,
        IConfiguration configuration)
    {
        _scyllaSessionProvider = scyllaSessionProvider;
        _options = options;
        _secretsStore = secretsStore;
        _configuration = configuration;
    }
    
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = new CancellationToken())
    {
        try
        {
            return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
            {
                var session = await _scyllaSessionProvider.GetSessionAsync(cancellationToken);

                var regionalKeyspace = await ScyllaConfigResolver.GetKeyspaceAsync(_configuration, _secretsStore, cancellationToken);

                var missingGlobal = await GetMissingTablesAsync(
                    session,
                    GlobalKeyspace,
                    RequiredGlobalScyllaTables);

                var missingRegional = await GetMissingTablesAsync(
                    session,
                    regionalKeyspace,
                    RequiredRegionalScyllaTables);

                if (missingGlobal.Count <= 0 && missingRegional.Count <= 0)
                {
                    return new HealthCheckResult(HealthStatus.Healthy, "Connected and required keyspace/tables exist.");
                }

                var details = new List<string>(2);
                if (missingGlobal.Count > 0)
                {
                    details.Add($"{GlobalKeyspace}: {string.Join(", ", missingGlobal)}");
                }

                if (missingRegional.Count > 0)
                {
                    details.Add($"{regionalKeyspace}: {string.Join(", ", missingRegional)}");
                }

                return new HealthCheckResult(HealthStatus.Degraded,
                    $"Scylla is reachable but missing canonical tables ({string.Join(" | ", details)}). Run the canonical CQL bootstrap script first.");
            }, _options, cancellationToken);
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(HealthStatus.Unhealthy, exception: ex);
        }
    }

    private static async Task<List<string>> GetMissingTablesAsync(ISession session, string keyspace, IEnumerable<string> tableNames)
    {
        //TODO: This should be optimized to query system_schema.tables once per keyspace and check for all required tables in-memory, 
        // rather than querying for each table individually.
        var missingTables = new List<string>();
        foreach (var tableName in tableNames)
        {
            var tableQuery = new SimpleStatement(
                "SELECT table_name FROM system_schema.tables WHERE keyspace_name = ? AND table_name = ? LIMIT 1",
                keyspace,
                tableName
            );

            var tableResult = await session.ExecuteAsync(tableQuery);
            if (!tableResult.Any())
            {
                missingTables.Add(tableName);
            }
        }

        return missingTables;
    }
}