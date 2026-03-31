using Npgsql;
using Interfold.Infrastructure.Persistence.Transient;

namespace Interfold.Infrastructure.Persistence.Postgres;

public interface IPostgresConnectionFactory
{
    Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}

public sealed class PostgresConnectionFactory : IPostgresConnectionFactory
{
    private readonly string _connectionString;
    private readonly PersistenceRegistrationOptions _options;

    public PostgresConnectionFactory(string connectionString, PersistenceRegistrationOptions options)
    {
        _connectionString = connectionString;
        _options = options;
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecutePostgresAsync(async () =>
        {
            var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }, _options, cancellationToken);
    }
}