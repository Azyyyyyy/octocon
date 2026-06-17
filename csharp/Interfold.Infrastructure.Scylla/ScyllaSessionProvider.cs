using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace Interfold.Infrastructure.Scylla;

public interface IScyllaSessionProvider
{
    Task<ISession> GetSessionAsync(CancellationToken cancellationToken = default);
}

public sealed class ScyllaSessionProvider : IScyllaSessionProvider
{
    private readonly PersistenceConfiguration _options;
    private readonly ISecretsStore _secretsStore;
    private readonly IConfiguration _configuration;
    private readonly Lazy<Task<ISession>> _session;

    public ScyllaSessionProvider(PersistenceConfiguration options, ISecretsStore secretsStore, IConfiguration configuration)
    {
        _options = options;
        _secretsStore = secretsStore;
        _configuration = configuration;
        _session = new Lazy<Task<ISession>>(ConnectAsync);
    }

    public Task<ISession> GetSessionAsync(CancellationToken cancellationToken = default) => _session.Value;

    private async Task<ISession> ConnectAsync()
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var contactPoints = await ScyllaConfigResolver.GetContactPointsAsync(_configuration, _secretsStore);
            var datacenter = await ScyllaConfigResolver.GetDatacenterAsync(_secretsStore);
            var username = await ScyllaConfigResolver.GetUsernameAsync(_secretsStore);
            var password = await ScyllaConfigResolver.GetPasswordAsync(_secretsStore);
            var keyspace = await ScyllaConfigResolver.GetKeyspaceAsync(_configuration);
            var port = await ScyllaConfigResolver.GetPortAsync(_configuration, _secretsStore);

            var builder = Cluster.Builder()
                .AddContactPoints(contactPoints)
                .WithPort(port)
                .WithLoadBalancingPolicy(new DCAwareRoundRobinPolicy(datacenter))
                .WithReconnectionPolicy(new ExponentialReconnectionPolicy(1000, 30000))
                .WithQueryTimeout(10000)
                .WithSocketOptions(new SocketOptions()
                    .SetConnectTimeoutMillis(10000)
                    .SetKeepAlive(true));

            if (!string.IsNullOrWhiteSpace(username))
            {
                builder = builder.WithCredentials(username, password);
            }

            var cluster = builder.Build();
            return await cluster.ConnectAsync(keyspace);
        }, _options);
    }
}
