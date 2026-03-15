using Octocon.Domain.Settings;
using Cassandra;
using Octocon.Domain.Abstractions;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaNotificationTokenRepository : INotificationTokenRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IRegionContext _regionContext;
    private readonly PersistenceRegistrationOptions _options;

    public ScyllaNotificationTokenRepository(
        IScyllaSessionProvider sessionProvider,
        IRegionContext regionContext,
        PersistenceRegistrationOptions options)
    {
        _sessionProvider = sessionProvider;
        _regionContext = regionContext;
        _options = options;
    }

    public async Task<bool> AddAsync(string systemId, string token, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var scopedSystemId = GetScopedSystemId(systemId);

            var writeBySystem = await session.PrepareAsync(@"
                INSERT INTO notification_tokens_by_system (system_id, push_token, inserted_at)
                VALUES (?, ?, ?)");

            var writeByToken = await session.PrepareAsync(@"
                INSERT INTO notification_tokens_by_token (push_token, system_id, inserted_at)
                VALUES (?, ?, ?)");

            await session.ExecuteAsync(writeBySystem.Bind(scopedSystemId, token, now.UtcDateTime));
            await session.ExecuteAsync(writeByToken.Bind(token, scopedSystemId, now.UtcDateTime));

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> RemoveAsync(string token, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);

            var findByToken = await session.PrepareAsync(@"
                SELECT system_id FROM notification_tokens_by_token
                WHERE push_token = ?");

            var existing = await session.ExecuteAsync(findByToken.Bind(token));
            var row = existing.FirstOrDefault();
            var systemId = row?.GetValue<string>("system_id");

            var deleteByToken = await session.PrepareAsync(@"
                DELETE FROM notification_tokens_by_token
                WHERE push_token = ?");

            await session.ExecuteAsync(deleteByToken.Bind(token));

            if (!string.IsNullOrWhiteSpace(systemId))
            {
                var deleteBySystem = await session.PrepareAsync(@"
                    DELETE FROM notification_tokens_by_system
                    WHERE system_id = ? AND push_token = ?");

                await session.ExecuteAsync(deleteBySystem.Bind(systemId, token));
            }

            return true;
        }, _options, cancellationToken);
    }

    private string GetScopedSystemId(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}
