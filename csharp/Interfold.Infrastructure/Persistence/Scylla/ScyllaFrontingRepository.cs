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

            var startBatch = new BatchStatement();
            startBatch.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.current_fronts (user_id, alter_id, id, comment, time_start, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId,
                (short)alterId,
                frontGuid,
                comment,
                startedAt,
                startedAt,
                startedAt
            ));
            startBatch.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.fronts (user_id, id, alter_id, comment, time_start, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId,
                frontGuid,
                (short)alterId,
                comment,
                startedAt,
                startedAt,
                startedAt
            ));
            await session.ExecuteAsync(startBatch);
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

            // Prefetch primary_front to include it in batch
            var primaryRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT primary_front FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();
            var primaryAlterId = primaryRow?.GetValue<int?>("primary_front");

            var endedAt = DateTimeOffset.UtcNow;

            var endBatch = new BatchStatement();
            endBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.fronts SET time_end = ?, updated_at = ? WHERE user_id = ? AND id = ? AND time_start = ?",
                endedAt,
                endedAt,
                normalizedSystemId,
                current.FrontId,
                current.StartedAt));
            endBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                (short)alterId));
            
            // Batch the conditional UPDATE into the same batch if needed
            if (primaryAlterId == alterId)
            {
                endBatch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.users SET primary_front = null, updated_at = toTimestamp(now()) WHERE id = ?",
                    normalizedSystemId));
            }
            
            await session.ExecuteAsync(endBatch);

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
                var frontingRow = (await session.ExecuteAsync(new SimpleStatement(
                    $"SELECT alter_id FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ? LIMIT 1",
                    normalizedSystemId,
                    (short)value))).FirstOrDefault();
                if (frontingRow is null)
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

            // Register UDT mapping before selecting the fields UDT column.
            ScyllaAlterRepository.EnsureAlterFieldUdtMapping(session, keyspace);

            // Parallelize the independent SELECT queries
            var primaryTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT primary_front FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            ));
            
            var activeTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT alter_id, id, comment, time_start FROM {keyspace}.current_fronts WHERE user_id = ?",
                normalizedSystemId
            ));
            
            var altersTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, name, avatar_url, description, color, fields, pronouns, pinned FROM {keyspace}.alters WHERE user_id = ?",
                normalizedSystemId));

            await Task.WhenAll(primaryTask, activeTask, altersTask);

            var primaryRow = (await primaryTask).FirstOrDefault();
            var primaryAlterId = primaryRow?.GetValue<int?>("primary_front");
            
            var rows = await activeTask;
            var alterRows = await altersTask;

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
        if (!Guid.TryParse(frontId, out var frontGuid))
            return null;

        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            // Identify the alter for this specific front record
            var frontRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.fronts WHERE user_id = ? AND id = ? LIMIT 1 ALLOW FILTERING",
                normalizedSystemId,
                frontGuid))).FirstOrDefault();

            if (frontRow is null)
                return null;

            var alterId = frontRow.GetValue<short>("alter_id");

            ScyllaAlterRepository.EnsureAlterFieldUdtMapping(session, keyspace);
            var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);

            // Parallelize the three remaining targeted lookups
            var currentTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, alter_id, comment, time_start FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ? LIMIT 1",
                normalizedSystemId, alterId));

            var alterTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, name, avatar_url, description, color, fields, pronouns, pinned FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId, alterId));

            var primaryTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT primary_front FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId));

            await Task.WhenAll(currentTask, alterTask, primaryTask);

            var currentRow = (await currentTask).FirstOrDefault();
            if (currentRow is null || currentRow.GetValue<Guid>("id") != frontGuid)
                return null;

            var alterRow = (await alterTask).FirstOrDefault();
            var primaryAlterId = (await primaryTask).FirstOrDefault()?.GetValue<int?>("primary_front");

            BareAlter alter;
            if (alterRow is null)
            {
                alter = new BareAlter(alterId, $"Alter {alterId}", null, null, null, null, null!);
            }
            else
            {
                alter = new BareAlter(
                    alterRow.GetValue<short>("id"),
                    alterRow.GetValue<string>("name"),
                    alterRow.GetValue<string?>("avatar_url"),
                    alterRow.GetValue<string?>("color"),
                    alterRow.GetValue<string?>("pronouns"),
                    alterRow.GetValue<string?>("description"),
                    ResolveFields(alterRow.GetValue<IEnumerable<AlterFieldUdt>?>("fields"), definitions));
            }

            var timeStart = currentRow.GetValue<DateTimeOffset?>("time_start") ?? DateTimeOffset.UtcNow;
            var front = new FrontHistoryReadModel(
                frontGuid.ToString("N"),
                alterId,
                currentRow.GetValue<string?>("comment"),
                timeStart,
                null,
                normalizedSystemId);

            return new FrontActiveReadModel(alter, front, primaryAlterId == (int)alterId);
        }, _options, cancellationToken, _logger);
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
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!Guid.TryParse(frontId, out var frontGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var current = await GetActiveFrontReferenceByFrontIdAsync(session, keyspace, normalizedSystemId, frontGuid);
            if (current is null)
            {
                return false;
            }

            var primaryRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT primary_front FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();
            var primaryAlterId = primaryRow?.GetValue<int?>("primary_front");

            var endedAt = DateTimeOffset.UtcNow;

            var endBatch = new BatchStatement();
            endBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.fronts SET time_end = ?, updated_at = ? WHERE user_id = ? AND id = ? AND time_start = ?",
                endedAt,
                endedAt,
                normalizedSystemId,
                current.FrontId,
                current.StartedAt));
            endBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                current.AlterId));

            if (primaryAlterId == current.AlterId)
            {
                endBatch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.users SET primary_front = null, updated_at = toTimestamp(now()) WHERE id = ?",
                    normalizedSystemId));
            }

            await session.ExecuteAsync(endBatch);
            return true;
        }, _options, cancellationToken, _logger);
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

            // Prefetch current front and primary info to batch operations
            var currentRow = await GetCurrentFrontRowAsync(session, keyspace, normalizedSystemId, alterId);
            var primaryRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT primary_front FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();
            var primaryAlterId = primaryRow?.GetValue<int?>("primary_front");

            // Build batch for all delete/update operations
            var deleteBatch = new BatchStatement();
            
            // Remove from current_fronts if still active
            if (currentRow is not null && currentRow.FrontId == Guid.Parse(frontId))
            {
                deleteBatch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ?",
                    normalizedSystemId,
                    alterId));
                
                // Batch the conditional primary_front update if this was the primary
                if (primaryAlterId == (int)alterId)
                {
                    deleteBatch.Add(new SimpleStatement(
                        $"UPDATE {keyspace}.users SET primary_front = null, updated_at = toTimestamp(now()) WHERE id = ?",
                        normalizedSystemId));
                }
            }

            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.fronts WHERE user_id = ? AND id = ? AND time_start = ?",
                normalizedSystemId,
                Guid.Parse(frontId),
                timeStart));
            
            await session.ExecuteAsync(deleteBatch);

            return true;
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> UpdateCommentByFrontIdAsync(string systemId, string frontId, string comment, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!Guid.TryParse(frontId, out var frontGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var current = await GetActiveFrontReferenceByFrontIdAsync(session, keyspace, normalizedSystemId, frontGuid);
            if (current is null)
                return false;

            var commentBatch = new BatchStatement();
            commentBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.current_fronts SET comment = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND alter_id = ?",
                comment,
                normalizedSystemId,
                current.AlterId
            ));
            commentBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.fronts SET comment = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ? AND time_start = ?",
                comment,
                normalizedSystemId,
                current.FrontId,
                current.StartedAt));
            await session.ExecuteAsync(commentBatch);
            return true;
        }, _options, cancellationToken, _logger);
    }

    private static async Task<CurrentFrontRow?> GetActiveFrontReferenceByFrontIdAsync(
        ISession session,
        string keyspace,
        string normalizedSystemId,
        Guid frontId)
    {
        var frontRow = (await session.ExecuteAsync(new SimpleStatement(
            $"SELECT alter_id FROM {keyspace}.fronts WHERE user_id = ? AND id = ? LIMIT 1 ALLOW FILTERING",
            normalizedSystemId,
            frontId))).FirstOrDefault();

        if (frontRow is null)
        {
            return null;
        }

        var alterId = frontRow.GetValue<short>("alter_id");
        var current = await GetCurrentFrontRowAsync(session, keyspace, normalizedSystemId, alterId);
        if (current is null || current.FrontId != frontId)
        {
            return null;
        }

        return current;
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
