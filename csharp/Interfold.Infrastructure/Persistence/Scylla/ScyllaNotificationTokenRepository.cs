using Interfold.Infrastructure.Configuration;
using Interfold.Domain.Settings;
using Interfold.Infrastructure.Persistence.Transient;

namespace Interfold.Infrastructure.Persistence.Scylla;

public sealed class ScyllaNotificationTokenRepository : INotificationTokenRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;

    public ScyllaNotificationTokenRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceConfiguration options)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
    }

    public async Task<bool> AddAsync(string systemId, string token, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);

            var write = await session.PrepareAsync(@"
                INSERT INTO global.notification_tokens (user_id, push_token, inserted_at, updated_at)
                VALUES (?, ?, ?, ?)");

            await session.ExecuteAsync(write.Bind(normalizedSystemId, token, now.UtcDateTime, now.UtcDateTime));
            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> RemoveAsync(string token, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);

            var findByToken = await session.PrepareAsync(@"
                SELECT user_id, push_token FROM global.notification_tokens
                WHERE push_token = ?");

            var rows = await session.ExecuteAsync(findByToken.Bind(token));
            foreach (var row in rows)
            {
                var userId = row.GetValue<string>("user_id");
                var pushToken = row.GetValue<string>("push_token");

                var deleteByPrimaryKey = await session.PrepareAsync(@"
                    DELETE FROM global.notification_tokens
                    WHERE user_id = ? AND push_token = ?");

                await session.ExecuteAsync(deleteByPrimaryKey.Bind(userId, pushToken));
            }

            return true;
        }, _options, cancellationToken);
    }
}
