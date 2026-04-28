using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Infrastructure.Persistence;

namespace Interfold.Infrastructure.Scylla;

public interface IScyllaSessionProvider
{
    Task<ISession> GetSessionAsync(CancellationToken cancellationToken = default);
}

public sealed class ScyllaSessionProvider : IScyllaSessionProvider
{
    private readonly PersistenceConfiguration _options;
    private readonly Lazy<Task<ISession>> _session;

    public ScyllaSessionProvider(PersistenceConfiguration options)
    {
        _options = options;
        _session = new Lazy<Task<ISession>>(ConnectAsync);
    }

    public Task<ISession> GetSessionAsync(CancellationToken cancellationToken = default) => _session.Value;

    private async Task<ISession> ConnectAsync()
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var builder = Cluster.Builder()
                .AddContactPoints(_options.ScyllaContactPoints)
                .WithLoadBalancingPolicy(new DCAwareRoundRobinPolicy(_options.ScyllaLocalDatacenter))
                .WithReconnectionPolicy(new ExponentialReconnectionPolicy(1000, 30000))
                .WithQueryTimeout(10000)
                .WithSocketOptions(new SocketOptions()
                    .SetConnectTimeoutMillis(10000)
                    .SetKeepAlive(true));

            if (!string.IsNullOrWhiteSpace(_options.ScyllaUsername))
            {
                builder = builder.WithCredentials(_options.ScyllaUsername, _options.ScyllaPassword ?? string.Empty);
            }

            var cluster = builder.Build();
            return await cluster.ConnectAsync(_options.ScyllaKeyspace);
        }, _options);
    }
}
