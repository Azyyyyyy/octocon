using Cassandra;
using Npgsql;
using Octocon.Infrastructure.Persistence.Postgres;
using Octocon.Infrastructure.Persistence.Scylla;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Bootstrap;

public sealed class ScyllaPostgresBootstrapHealthChecker : IDatabaseBootstrapHealthChecker
{
    private const string GlobalKeyspace = "global";

    private static readonly string[] RequiredGlobalScyllaTables =
    [
        "users",
        "user_registry",
        "notification_tokens",
        "friendships",
        "friend_requests",
        "aggregate_versions_by_region"
    ];

    private static readonly string[] RequiredRegionalScyllaTables =
    [
        "alters",
        "tags",
        "alter_tags",
        "polls",
        "fronts",
        "current_fronts",
        "global_journals",
        "global_journal_alters",
        "alter_journals",
        "settings_fields"
    ];

    private readonly IPostgresConnectionFactory _postgresConnectionFactory;
    private readonly IScyllaSessionProvider _scyllaSessionProvider;
    private readonly PersistenceRegistrationOptions _options;

    public ScyllaPostgresBootstrapHealthChecker(
        IPostgresConnectionFactory postgresConnectionFactory,
        IScyllaSessionProvider scyllaSessionProvider,
        PersistenceRegistrationOptions options
    )
    {
        _postgresConnectionFactory = postgresConnectionFactory;
        _scyllaSessionProvider = scyllaSessionProvider;
        _options = options;
    }

    public async Task<DatabaseBootstrapHealthResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var stores = new List<DatabaseStoreHealth>(2)
        {
            await CheckPostgresAsync(cancellationToken),
            await CheckScyllaAsync(cancellationToken)
        };

        return new DatabaseBootstrapHealthResult(stores.All(store => store.Healthy), stores);
    }

    private async Task<DatabaseStoreHealth> CheckPostgresAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DatabaseTransientRetry.ExecutePostgresAsync(async () =>
            {
                await using var connection = await _postgresConnectionFactory.OpenConnectionAsync(cancellationToken);

                await using var pingCommand = new NpgsqlCommand("SELECT 1", connection);
                await pingCommand.ExecuteScalarAsync(cancellationToken);

                await using var tableCommand = new NpgsqlCommand(
                    "SELECT to_regclass('public.octocon_idempotency')::text",
                    connection
                );

                var table = await tableCommand.ExecuteScalarAsync(cancellationToken) as string;
                if (string.IsNullOrWhiteSpace(table))
                {
                    throw new InvalidOperationException(
                        "Postgres table 'octocon_idempotency' was not found. Run the SQL bootstrap script first."
                    );
                }
            }, _options, cancellationToken);

            return new DatabaseStoreHealth("postgres", true, "Connected and required table exists.");
        }
        catch (Exception ex)
        {
            return new DatabaseStoreHealth("postgres", false, ex.Message);
        }
    }

    private async Task<DatabaseStoreHealth> CheckScyllaAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
            {
                var session = await _scyllaSessionProvider.GetSessionAsync(cancellationToken);
                var regionalKeyspace = _options.ScyllaKeyspace;

                var missingGlobal = await GetMissingTablesAsync(
                    session,
                    GlobalKeyspace,
                    RequiredGlobalScyllaTables);

                var missingRegional = await GetMissingTablesAsync(
                    session,
                    regionalKeyspace,
                    RequiredRegionalScyllaTables);

                if (missingGlobal.Count > 0 || missingRegional.Count > 0)
                {
                    var details = new List<string>(2);
                    if (missingGlobal.Count > 0)
                    {
                        details.Add($"{GlobalKeyspace}: {string.Join(", ", missingGlobal)}");
                    }

                    if (missingRegional.Count > 0)
                    {
                        details.Add($"{regionalKeyspace}: {string.Join(", ", missingRegional)}");
                    }

                    throw new InvalidOperationException(
                        $"Scylla is reachable but missing canonical tables ({string.Join(" | ", details)}). Run the canonical CQL bootstrap script first.");
                }
            }, _options, cancellationToken);

            return new DatabaseStoreHealth("scylla", true, "Connected and required keyspace/tables exist.");
        }
        catch (Exception ex)
        {
            return new DatabaseStoreHealth("scylla", false, ex.Message);
        }
    }

    private static async Task<List<string>> GetMissingTablesAsync(ISession session, string keyspace, IEnumerable<string> tableNames)
    {
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
