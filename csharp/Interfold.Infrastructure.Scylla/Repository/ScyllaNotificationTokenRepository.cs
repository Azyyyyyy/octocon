using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;

namespace Interfold.Infrastructure.Scylla.Repository;

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

            var addtion = await session.ExecuteAsync(write.Bind(normalizedSystemId, token, now.UtcDateTime, now.UtcDateTime));
            return addtion is not null;
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
            
            // Batch all deletes instead of executing individually, 
            // in case multiple entries exist with the same token (shouldn't happen, but just in case)
            var deleteBatch = new BatchStatement();
            foreach (var row in rows)
            {
                deleteBatch.Add(new SimpleStatement(
                    "DELETE FROM global.notification_tokens WHERE user_id = ? AND push_token = ?",
                    row.GetValue<string>("user_id"),
                    row.GetValue<string>("push_token")));
            }

            if (!deleteBatch.IsEmpty)
            {
                await session.ExecuteAsync(deleteBatch);                
            }

            return true;
        }, _options, cancellationToken);
    }
}
