using Cassandra;
using Npgsql;
using Octocon.Infrastructure.Persistence.Postgres;
using Octocon.Infrastructure.Persistence.Scylla;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Bootstrap;

public sealed class ScyllaPostgresBootstrapHealthChecker : IDatabaseBootstrapHealthChecker
{
    private static readonly string[] RequiredScyllaTables =
    [
        "account_profiles_by_system",
        "alters_by_system",
        "fronting_active_by_system",
        "fronting_primary_by_system",
        "aggregate_versions_by_region"
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

                var missingTables = new List<string>();
                foreach (var tableName in RequiredScyllaTables)
                {
                    var tableQuery = new SimpleStatement(
                        "SELECT table_name FROM system_schema.tables WHERE keyspace_name = ? AND table_name = ? LIMIT 1",
                        _options.ScyllaKeyspace,
                        tableName
                    );

                    var tableResult = await session.ExecuteAsync(tableQuery);
                    if (!tableResult.Any())
                    {
                        missingTables.Add(tableName);
                    }
                }

                if (missingTables.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Scylla keyspace '{_options.ScyllaKeyspace}' is reachable but missing tables: {string.Join(", ", missingTables)}. Run the CQL bootstrap script first."
                    );
                }
            }, _options, cancellationToken);

            return new DatabaseStoreHealth("scylla", true, "Connected and required keyspace/tables exist.");
        }
        catch (Exception ex)
        {
            return new DatabaseStoreHealth("scylla", false, ex.Message);
        }
    }
}
