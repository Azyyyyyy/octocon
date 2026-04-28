using Interfold.Contracts.Configuration;
using Interfold.Infrastructure.Persistence;
using Npgsql;

namespace Interfold.Infrastructure.Postgres;

public interface IPostgresConnectionFactory
{
    Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}

public sealed class PostgresConnectionFactory : IPostgresConnectionFactory
{
    private readonly PersistenceConfiguration _options;

    public PostgresConnectionFactory(PersistenceConfiguration options)
    {
        _options = options;
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecutePostgresAsync(async () =>
        {
            var connection = new NpgsqlConnection(_options.PostgresConnectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }, _options, cancellationToken);
    }
}
