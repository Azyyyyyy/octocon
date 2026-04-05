using Cassandra;
using Interfold.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Interfold.Domain.Alters;
using Interfold.Domain.Fronting;
using Interfold.Domain.Settings;
using Interfold.Infrastructure.Persistence.Transient;
using static Interfold.Infrastructure.Persistence.Scylla.ScyllaAlterRepository;

namespace Interfold.Infrastructure.Persistence.Scylla;

public sealed class ScyllaFrontingRepository : IFrontingRepository
{
    private sealed record CurrentFrontRow(Guid FrontId, short AlterId, DateTimeOffset StartedAt);

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;
    private readonly ILogger<ScyllaFrontingRepository> _logger;
    private readonly ISettingsFieldRepository _settingsFields;

    public ScyllaFrontingRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceConfiguration options,
        ILogger<ScyllaFrontingRepository> logger,
        ISettingsFieldRepository settingsFields
    )
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
        _logger = logger;
        _settingsFields = settingsFields;
    }

    public async Task<bool> IsFrontingAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ? LIMIT 1",
                normalizedSystemId,
                (short)alterId
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any();
        }, _options, cancellationToken, _logger);
    }

    public async Task<string?> StartAsync(
        string systemId,
        int alterId,
        string? comment,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var frontGuid = Guid.NewGuid();
            var startedAt = DateTimeOffset.UtcNow;

            var insertCurrent = new SimpleStatement(
                $"INSERT INTO {keyspace}.current_fronts (user_id, alter_id, id, comment, time_start, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId,
                (short)alterId,
                frontGuid,
                comment,
                startedAt,
                startedAt,
                startedAt
            );

            await session.ExecuteAsync(insertCurrent);

            var insertHistory = new SimpleStatement(
                $"INSERT INTO {keyspace}.fronts (user_id, id, alter_id, comment, time_start, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId,
                frontGuid,
                (short)alterId,
                comment,
                startedAt,
                startedAt,
                startedAt
            );

            await session.ExecuteAsync(insertHistory);
            return frontGuid.ToString("N");
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> EndAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var current = await GetCurrentFrontRowAsync(session, keyspace, normalizedSystemId, (short)alterId);
            if (current is null)
            {
                return false;
            }

            var endedAt = DateTimeOffset.UtcNow;

            await session.ExecuteAsync(new SimpleStatement(
                $"UPDATE {keyspace}.fronts SET time_end = ?, updated_at = ? WHERE user_id = ? AND id = ? AND time_start = ?",
                endedAt,
                endedAt,
                normalizedSystemId,
                current.FrontId,
                current.StartedAt));

            await session.ExecuteAsync(new SimpleStatement(
                $"DELETE FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                (short)alterId));

            var primaryRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT primary_front FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();
            if (primaryRow?.GetValue<int?>("primary_front") == alterId)
            {
                await session.ExecuteAsync(new SimpleStatement(
                    $"UPDATE {keyspace}.users SET primary_front = null, updated_at = toTimestamp(now()) WHERE id = ?",
                    normalizedSystemId));
            }

            return true;
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> SetPrimaryAsync(string systemId, int? alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            if (alterId is int value)
            {
                var fronting = await IsFrontingAsync(systemId, value, cancellationToken);
                if (!fronting)
                {
                    return false;
                }

                await session.ExecuteAsync(new SimpleStatement(
                    $"UPDATE {keyspace}.users SET primary_front = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                    value,
                    normalizedSystemId));

                return true;
            }

            await session.ExecuteAsync(new SimpleStatement(
                $"UPDATE {keyspace}.users SET primary_front = null, updated_at = toTimestamp(now()) WHERE id = ?",
                normalizedSystemId));

            return true;
        }, _options, cancellationToken, _logger);
    }

    public async Task<IReadOnlyList<FrontActiveReadModel>> ListActiveAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var primaryQuery = new SimpleStatement(
                $"SELECT primary_front FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            );

            var primaryRow = (await session.ExecuteAsync(primaryQuery)).FirstOrDefault();
            var primaryAlterId = primaryRow?.GetValue<int?>("primary_front");

            var activeQuery = new SimpleStatement(
                $"SELECT alter_id, id, comment, time_start FROM {keyspace}.current_fronts WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(activeQuery);
            var alterRows = await session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, name, avatar_url, description, color, fields, pronouns, pinned FROM {keyspace}.alters WHERE user_id = ?",
                normalizedSystemId));

            ScyllaAlterRepository.EnsureAlterFieldUdtMapping(session, keyspace);
            var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);

            var alterById = alterRows.ToDictionary(
                row => (int)row.GetValue<short>("id"),
                row => new BareAlter(
                    row.GetValue<short>("id"),
                    row.GetValue<string>("name"),
                    row.GetValue<string?>("avatar_url"),
                    row.GetValue<string?>("color"),
                    row.GetValue<string?>("pronouns"),
                    row.GetValue<string?>("description"),
                    ResolveFields(row.GetValue<IEnumerable<AlterFieldUdt>?>("fields"), definitions)));

            return (IReadOnlyList<FrontActiveReadModel>)rows
                .Select(row =>
                {
                    var frontId = row.GetValue<Guid>("id").ToString("N");
                    var alterId = row.GetValue<short>("alter_id");
                    var timeStart = row.GetValue<DateTimeOffset?>("time_start") ?? DateTimeOffset.UtcNow;
                    var front = new FrontHistoryReadModel(
                        frontId,
                        alterId,
                        row.GetValue<string?>("comment"),
                        timeStart,
                        null,
                        normalizedSystemId);

                    if (!alterById.TryGetValue(alterId, out var alter))
                    {
                        alter = new BareAlter(alterId, $"Alter {alterId}", null, null, null, null, null!);
                    }

                    return new FrontActiveReadModel(alter, front, primaryAlterId == alterId);
                })
                .OrderByDescending(x => x.Front.TimeStart)
                .ToArray();
        }, _options, cancellationToken, _logger);
    }

    private static IReadOnlyList<AlterPublicFieldReadModel> ResolveFields(
        IEnumerable<AlterFieldUdt>? alterFields,
        IReadOnlyList<SettingsFieldReadModel> definitions)
    {
        if (definitions.Count == 0)
        {
            return [];
        }

        return definitions
            .Select(def => new AlterPublicFieldReadModel(def.Id, def.Name, def.Type, alterFields?.FirstOrDefault(x => x.Id.ToString("N") == def.Id)?.Value))
            .ToArray();
    }

    public async Task<IReadOnlyList<FrontActiveReadModel>> ListActiveGuardedAsync(
        string systemId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var friendshipLevel = await ResolveFriendshipLevelAsync(session, normalizedSystemId, viewerSystemId);

            var all = await ListActiveAsync(systemId, cancellationToken);
            if (all.Count == 0)
            {
                return all;
            }

            var securityRows = await session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, security_level FROM {keyspace}.alters WHERE user_id = ?",
                normalizedSystemId));

            var visibilityByAlterId = securityRows.ToDictionary(
                row => (int)row.GetValue<short>("id"),
                row => ResolveVisibilityLevel(row.GetValue<short?>("security_level")));

            var filtered = all
                .Where(front => visibilityByAlterId.TryGetValue(front.Alter.Id, out var level) && CanView(friendshipLevel, level))
                .ToArray();

            return (IReadOnlyList<FrontActiveReadModel>)filtered;
        }, _options, cancellationToken, _logger);
    }

    public async Task<IReadOnlyList<FrontHistoryReadModel>> ListHistoryBetweenAsync(
        string systemId,
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var historyQuery = new SimpleStatement(
                $"SELECT id, alter_id, comment, time_start, time_end FROM {keyspace}.fronts_by_time WHERE user_id = ? AND time_start >= ? AND time_start <= ?",
                normalizedSystemId,
                startInclusive,
                endInclusive
            );

            var historicalRows = await session.ExecuteAsync(historyQuery);
            var historical = historicalRows
                .Select(row => new FrontHistoryReadModel(
                    row.GetValue<Guid>("id").ToString("N"),
                    row.GetValue<short>("alter_id"),
                    row.GetValue<string?>("comment"),
                    row.GetValue<DateTimeOffset>("time_start"),
                    row.GetValue<DateTimeOffset?>("time_end"),
                    normalizedSystemId))
                .Where(x => x.TimeEnd != null)
                .ToList();

            return (IReadOnlyList<FrontHistoryReadModel>)historical
                .DistinctBy(x => x.Id)
                .OrderByDescending(x => x.TimeStart)
                .ToArray();
        }, _options, cancellationToken, _logger);
    }

    public async Task<FrontActiveReadModel?> GetActiveByFrontIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default)
    {
        var active = await ListActiveAsync(systemId, cancellationToken);
        return active.FirstOrDefault(x => string.Equals(x.Front.Id, frontId, StringComparison.Ordinal));
    }

    public async Task<FrontHistoryReadModel?> GetHistoryEntryByFrontIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var row = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, alter_id, comment, time_start, time_end FROM {keyspace}.fronts WHERE user_id = ? AND id = ? LIMIT 1 ALLOW FILTERING",
                normalizedSystemId,
                Guid.Parse(frontId)))).FirstOrDefault();

            if (row is null)
                return null;

            return new FrontHistoryReadModel(
                row.GetValue<Guid>("id").ToString("N"),
                row.GetValue<short>("alter_id"),
                row.GetValue<string?>("comment"),
                row.GetValue<DateTimeOffset>("time_start"),
                row.GetValue<DateTimeOffset?>("time_end"),
                normalizedSystemId);
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> EndByFrontIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default)
    {
        var existing = await GetActiveByFrontIdAsync(systemId, frontId, cancellationToken);
        if (existing is null)
            return false;

        return await EndAsync(systemId, existing.Front.AlterId, cancellationToken);
    }

    public async Task<bool> DeleteFrontByIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var row = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT alter_id, time_start FROM {keyspace}.fronts WHERE user_id = ? AND id = ? LIMIT 1 ALLOW FILTERING",
                normalizedSystemId,
                Guid.Parse(frontId)))).FirstOrDefault();

            if (row is null)
                return false;

            var alterId = row.GetValue<short>("alter_id");
            var timeStart = row.GetValue<DateTimeOffset>("time_start");

            // Remove from current_fronts if still active
            var currentRow = await GetCurrentFrontRowAsync(session, keyspace, normalizedSystemId, alterId);
            if (currentRow is not null && currentRow.FrontId == Guid.Parse(frontId))
            {
                await session.ExecuteAsync(new SimpleStatement(
                    $"DELETE FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ?",
                    normalizedSystemId,
                    alterId));

                var primaryRow = (await session.ExecuteAsync(new SimpleStatement(
                    $"SELECT primary_front FROM {keyspace}.users WHERE id = ? LIMIT 1",
                    normalizedSystemId))).FirstOrDefault();
                if (primaryRow?.GetValue<int?>("primary_front") == (int)alterId)
                {
                    await session.ExecuteAsync(new SimpleStatement(
                        $"UPDATE {keyspace}.users SET primary_front = null, updated_at = toTimestamp(now()) WHERE id = ?",
                        normalizedSystemId));
                }
            }

            await session.ExecuteAsync(new SimpleStatement(
                $"DELETE FROM {keyspace}.fronts WHERE user_id = ? AND id = ? AND time_start = ?",
                normalizedSystemId,
                Guid.Parse(frontId),
                timeStart));

            return true;
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> UpdateCommentByFrontIdAsync(string systemId, string frontId, string comment, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var existing = await GetActiveByFrontIdAsync(systemId, frontId, cancellationToken);
            if (existing is null)
                return false;

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var update = new SimpleStatement(
                $"UPDATE {keyspace}.current_fronts SET comment = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND alter_id = ?",
                comment,
                normalizedSystemId,
                (short)existing.Front.AlterId
            );

            await session.ExecuteAsync(update);

            var updateHistory = new SimpleStatement(
                $"UPDATE {keyspace}.fronts SET comment = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ? AND time_start = ?",
                comment,
                normalizedSystemId,
                Guid.Parse(existing.Front.Id),
                existing.Front.TimeStart);

            await session.ExecuteAsync(updateHistory);
            return true;
        }, _options, cancellationToken, _logger);
    }

    private static async Task<CurrentFrontRow?> GetCurrentFrontRowAsync(
        ISession session,
        string keyspace,
        string normalizedSystemId,
        short alterId)
    {
        var query = new SimpleStatement(
            $"SELECT id, alter_id, time_start FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ? LIMIT 1",
            normalizedSystemId,
            alterId);

        var row = (await session.ExecuteAsync(query)).FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        return new CurrentFrontRow(
            row.GetValue<Guid>("id"),
            row.GetValue<short>("alter_id"),
            row.GetValue<DateTimeOffset?>("time_start") ?? DateTimeOffset.UtcNow);
    }

    private async Task<string?> ResolveFriendshipLevelAsync(ISession session, string ownerSystemId, string? viewerSystemId)
    {
        if (string.IsNullOrWhiteSpace(viewerSystemId))
        {
            return null;
        }

        var normalizedViewerSystemId = _keyspaceResolver.NormalizeSystemId(viewerSystemId);
        if (string.Equals(ownerSystemId, normalizedViewerSystemId, StringComparison.Ordinal))
        {
            return "trusted_friend";
        }

        var query = new SimpleStatement(
            "SELECT level FROM global.friendships WHERE user_id = ? AND friend_id = ? LIMIT 1",
            ownerSystemId,
            normalizedViewerSystemId);

        var row = (await session.ExecuteAsync(query)).FirstOrDefault();
        return row is null ? null : ScyllaFriendshipRepository.ToDomainLevel(row.GetValue<short>("level"));
    }

    private static VisibilityLevel ResolveVisibilityLevel(short? value)
    {
        return value switch
        {
            1 => VisibilityLevel.FriendsOnly,
            2 => VisibilityLevel.TrustedOnly,
            3 => VisibilityLevel.Private,
            _ => VisibilityLevel.Public
        };
    }

    private static bool CanView(string? friendshipLevel, VisibilityLevel visibilityLevel)
    {
        return visibilityLevel switch
        {
            VisibilityLevel.Public => true,
            VisibilityLevel.FriendsOnly => friendshipLevel is "friend" or "trusted_friend",
            VisibilityLevel.TrustedOnly => friendshipLevel is "trusted_friend",
            _ => false
        };
    }
}
