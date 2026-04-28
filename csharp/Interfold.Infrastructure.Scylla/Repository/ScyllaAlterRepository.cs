using System.Collections.Concurrent;
using Cassandra;
using Interfold.Domain.Alters;
using Interfold.Domain.Polls;
using Interfold.Domain.Settings;
using Interfold.Infrastructure.Configuration;
using Interfold.Infrastructure.Persistence.Transient;
using Microsoft.Extensions.Logging;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaAlterRepository : IAlterRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly ISettingsFieldRepository _settingsFields;
    private readonly IPollRepository _pollRepository;
    private readonly PersistenceConfiguration _options;
    private readonly ILogger<ScyllaAlterRepository> _logger;
    private static readonly ConcurrentDictionary<string, byte> UdtMappings = new(StringComparer.OrdinalIgnoreCase);

    public ScyllaAlterRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        ISettingsFieldRepository settingsFields,
        IPollRepository pollRepository,
        PersistenceConfiguration options,
        ILogger<ScyllaAlterRepository> logger
    )
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _settingsFields = settingsFields;
        _pollRepository = pollRepository;
        _options = options;
        _logger = logger;
    }

    public async Task<int?> CreateAsync(string systemId, CreateAlterCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var nextIdQuery = new SimpleStatement(
                $"SELECT id FROM {keyspace}.alters WHERE user_id = ? ORDER BY id DESC LIMIT 1",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(nextIdQuery);
            var current = rows.FirstOrDefault()?.GetValue<short>("id") ?? (short)0;
            var next = (short)(current + 1);

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.alters (user_id, id, name, alias, inserted_at, updated_at) VALUES (?, ?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                normalizedSystemId,
                next,
                command.Name,
                null
            );

            await session.ExecuteAsync(insert);
            return (int?)next;
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> ExistsAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                (short)alterId
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any();
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> UpdateAsync(string systemId, UpdateAlterCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var batch = new BatchStatement();

            var exists = await ExistsAsync(session, keyspace, normalizedSystemId, (short)command.AlterId);
            if (!exists)
            {
                return false;
            }

            UpdateIfNotNull(batch, keyspace, command, "name", command.Name, normalizedSystemId);
            UpdateIfNotNull(batch, keyspace, command, "description", command.Description, normalizedSystemId);
            if (command.ClearAvatar)
            {
                batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET avatar_url = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    null,
                    normalizedSystemId,
                    (short)command.AlterId
                ));
            }
            else
            {
                UpdateIfNotNull(batch, keyspace, command, "avatar_url", command.AvatarUrl, normalizedSystemId);
            }
            UpdateIfNotNull(batch, keyspace, command, "color", command.Color, normalizedSystemId);
            UpdateIfNotNull(batch, keyspace, command, "pronouns", command.Pronouns, normalizedSystemId);
            UpdateIfNotNull(batch, keyspace, command, "security_level", command.SecurityLevel == null ? null : ToSecurityLevel(command.SecurityLevel), normalizedSystemId);

            if (command.Fields is not null)
            {
                EnsureAlterFieldUdtMapping(session, keyspace);

                var currentRow = (await session.ExecuteAsync(new SimpleStatement(
                    $"SELECT fields FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                    normalizedSystemId,
                    (short)command.AlterId
                ))).FirstOrDefault();

                var merged = (currentRow?.GetValue<IEnumerable<AlterFieldUdt>?>("fields") ?? [])
                    .ToDictionary(x => x.Id.ToString("N"), x => x.Value, StringComparer.OrdinalIgnoreCase);

                foreach (var f in command.Fields)
                {
                    merged[f.Id] = f.Value;
                }

                var udts = merged
                    .Select(kvp => new AlterFieldUdt { Id = Guid.Parse(kvp.Key), Value = kvp.Value })
                    .ToList();

                batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET fields = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    udts,
                    normalizedSystemId,
                    (short)command.AlterId
                ));
            }

            UpdateIfNotNull(batch, keyspace, command, "proxy_name", command.ProxyName, normalizedSystemId);
            UpdateIfNotNull(batch, keyspace, command, "alias", command.Alias, normalizedSystemId);
            UpdateIfNotNull(batch, keyspace, command, "untracked", command.Untracked, normalizedSystemId);
            UpdateIfNotNull(batch, keyspace, command, "archived", command.Archived, normalizedSystemId);
            UpdateIfNotNull(batch, keyspace, command, "pinned", command.Pinned, normalizedSystemId);

            await session.ExecuteAsync(batch);

            return true;
        }, _options, cancellationToken, _logger);
    }

    private void UpdateIfNotNull(BatchStatement batch, string keyspace, UpdateAlterCommand command, string field, object? value, string normalizedSystemId)
    {
        if (value is not null)
        {
            batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET {field} = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    value,
                    normalizedSystemId,
                    (short)command.AlterId
                ));
        }
    }

    public async Task<bool> DeleteAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var alterIdShort = (short)alterId;

            var exists = await ExistsAsync(session, keyspace, normalizedSystemId, alterIdShort);
            if (!exists)
            {
                return false;
            }

            var deleteBatch = new BatchStatement();
            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.alters WHERE user_id = ? AND id = ?",
                normalizedSystemId,
                alterIdShort));
            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                alterIdShort));
            await session.ExecuteAsync(deleteBatch);

            // Parallelize the three cascade queries
            var frontsTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, time_start FROM {keyspace}.fronts_by_alter WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                alterIdShort));

            var journalEntriesTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT id FROM {keyspace}.alter_journals_by_alter WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                alterIdShort));

            var primaryTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT primary_front FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId));

            var tagsTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT tag_id FROM {keyspace}.alter_tags_by_alter WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                alterIdShort));

            var globalJournalAltersTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT global_journal_id FROM {keyspace}.global_journal_alters WHERE user_id = ? AND alter_id = ? ALLOW FILTERING",
                normalizedSystemId,
                alterIdShort));

            var removePollsTask = _pollRepository.RemoveAlterFromPollsAsync(systemId, alterId, cancellationToken);

            await Task.WhenAll(
                frontsTask,
                journalEntriesTask,
                primaryTask,
                tagsTask,
                globalJournalAltersTask,
                removePollsTask
            );

            var frontRows = await frontsTask;
            var journalEntryRows = await journalEntriesTask;
            var primaryFrontRow = (await primaryTask).FirstOrDefault();
            var membershipRows = await tagsTask;
            var globalJournalAlterRows = await globalJournalAltersTask;

            // Batch all front deletes
            if (frontRows.Any())
            {
                var frontBatch = new BatchStatement();
                foreach (var frontRow in frontRows)
                {
                    frontBatch.Add(new SimpleStatement(
                        $"DELETE FROM {keyspace}.fronts WHERE user_id = ? AND id = ? AND time_start = ?",
                        normalizedSystemId,
                        frontRow.GetValue<Guid>("id"),
                        frontRow.GetValue<DateTimeOffset>("time_start")));
                }
                await session.ExecuteAsync(frontBatch);
            }

            // Batch all journal entry deletes
            if (journalEntryRows.Any())
            {
                var journalBatch = new BatchStatement();
                foreach (var row in journalEntryRows)
                {
                    journalBatch.Add(new SimpleStatement(
                        $"DELETE FROM {keyspace}.alter_journals WHERE user_id = ? AND id = ? AND alter_id = ?",
                        normalizedSystemId,
                        row.GetValue<Guid>("id"),
                        alterIdShort));
                }
                await session.ExecuteAsync(journalBatch);
            }

            // Handle primary_front update if needed
            if (primaryFrontRow?.GetValue<short?>("primary_front") == alterIdShort)
            {
                await session.ExecuteAsync(new SimpleStatement(
                    $"UPDATE {keyspace}.users SET primary_front = null WHERE id = ?",
                    normalizedSystemId));
            }

            // Batch all tag deletes
            if (membershipRows.Any())
            {
                var tagBatch = new BatchStatement();
                foreach (var row in membershipRows)
                {
                    var tagId = row.GetValue<Guid>("tag_id");
                    tagBatch.Add(new SimpleStatement(
                        $"DELETE FROM {keyspace}.alter_tags WHERE user_id = ? AND tag_id = ? AND alter_id = ?",
                        normalizedSystemId,
                        tagId,
                        alterIdShort));
                }
                await session.ExecuteAsync(tagBatch);
            }

            // Batch all global journal alter deletes
            if (globalJournalAlterRows.Any())
            {
                var gjaBatch = new BatchStatement();
                foreach (var row in globalJournalAlterRows)
                {
                    gjaBatch.Add(new SimpleStatement(
                        $"DELETE FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ? AND alter_id = ?",
                        normalizedSystemId,
                        row.GetValue<Guid>("global_journal_id"),
                        alterIdShort));
                }
                await session.ExecuteAsync(gjaBatch);
            }

            return true;
        }, _options, cancellationToken, _logger);
    }

    public async Task<IReadOnlyList<AlterReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);
            EnsureAlterFieldUdtMapping(session, keyspace);

            var query = new SimpleStatement(
                $"SELECT id, name, alias, fields, security_level, color, pronouns, avatar_url, pinned, archived, untracked, description, proxy_name FROM {keyspace}.alters WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            return rows
                .Select(row => new AlterReadModel(
                    row.GetValue<short>("id"),
                    row.GetValue<string>("name"),
                    row.GetValue<string?>("description"),
                    row.GetValue<string?>("avatar_url"),
                    row.GetValue<string?>("color"),
                    row.GetValue<string?>("pronouns"),
                    ResolveVisibilityLevel(row.GetValue<short?>("security_level")),
                    ResolveFields(row.GetValue<IEnumerable<AlterFieldUdt>?>("fields"), definitions),
                    row.GetValue<string?>("proxy_name"),
                    row.GetValue<string?>("alias"),
                    row.GetValue<bool?>("untracked"),
                    row.GetValue<bool?>("archived"),
                    row.GetValue<bool?>("pinned")
                ))
                .OrderBy(x => x.Id)
                .ToArray();
        }, _options, cancellationToken, _logger);
    }

    public async Task<IReadOnlyList<BareAlter>> ListGuardedAsync(
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
            EnsureAlterFieldUdtMapping(session, keyspace);
            var definitions = await ResolveVisibleDefinitionsAsync(systemId, friendshipLevel, cancellationToken);

            var query = new SimpleStatement(
                $"SELECT id, name, avatar_url, color, description, pronouns, pinned, security_level, fields FROM {keyspace}.alters WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            return rows
                .Where(row => CanView(friendshipLevel, ResolveVisibilityLevel(row.GetValue<short?>("security_level"))))
                .Select(row => new BareAlter(
                    row.GetValue<short>("id"),
                    row.GetValue<string>("name"),
                    row.GetValue<string?>("avatar_url"),
                    row.GetValue<string?>("color"),
                    row.GetValue<string?>("pronouns"),
                    row.GetValue<string?>("description"),
                    ResolveGuardedFields(row.GetValue<IEnumerable<AlterFieldUdt>?>("fields"), definitions)))
                .OrderBy(x => x.Id)
                .ToArray();
        }, _options, cancellationToken, _logger);
    }

    public async Task<AlterReadModel?> GetAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);
            EnsureAlterFieldUdtMapping(session, keyspace);

            var query = new SimpleStatement(
                $"SELECT id, name, alias, fields, security_level, color, pronouns, avatar_url, pinned, archived, untracked, description, proxy_name FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                (short)alterId
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null
                ? null
                : new AlterReadModel(
                    row.GetValue<short>("id"),
                    row.GetValue<string>("name"),
                    row.GetValue<string?>("description"),
                    row.GetValue<string?>("avatar_url"),
                    row.GetValue<string?>("color"),
                    row.GetValue<string?>("pronouns"),
                    ResolveVisibilityLevel(row.GetValue<short?>("security_level")),
                    ResolveFields(row.GetValue<IEnumerable<AlterFieldUdt>?>("fields"), definitions),
                    row.GetValue<string?>("proxy_name"),
                    row.GetValue<string?>("alias"),
                    row.GetValue<bool?>("untracked"),
                    row.GetValue<bool?>("archived"),
                    row.GetValue<bool?>("pinned")
                );
        }, _options, cancellationToken, _logger);
    }

    public async Task<BareAlter?> GetGuardedAsync(
        string systemId,
        int alterId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var friendshipLevel = await ResolveFriendshipLevelAsync(session, normalizedSystemId, viewerSystemId);
            EnsureAlterFieldUdtMapping(session, keyspace);
            var definitions = await ResolveVisibleDefinitionsAsync(systemId, friendshipLevel, cancellationToken);

            var query = new SimpleStatement(
                $"SELECT id, name, avatar_url, description, color, pronouns, pinned, security_level, fields FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                (short)alterId
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is null)
            {
                return null;
            }

            var securityLevel = ResolveVisibilityLevel(row.GetValue<short?>("security_level"));
            if (!CanView(friendshipLevel, securityLevel))
            {
                return null;
            }

            return new BareAlter(
                row.GetValue<short>("id"),
                row.GetValue<string>("name"),
                row.GetValue<string?>("avatar_url"),
                row.GetValue<string?>("color"),
                row.GetValue<string?>("pronouns"),
                row.GetValue<string?>("description"),
                ResolveGuardedFields(row.GetValue<IEnumerable<AlterFieldUdt>?>("fields"), definitions)
            );
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> AliasTakenByOtherAsync(
        string systemId,
        int alterId,
        string alias,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id, alias FROM {keyspace}.alters WHERE user_id = ? AND alias = ?",
                normalizedSystemId,
                alias
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any(row => row.GetValue<short>("id") != (short)alterId);
        }, _options, cancellationToken, _logger);
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
        // Treat null as public for backward compatibility with older rows.
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

    private static short ToSecurityLevel(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "friends_only" => 1,
            "trusted_only" => 2,
            "private" => 3,
            _ => 0
        };
    }

    private static async Task<bool> ExistsAsync(ISession session, string keyspace, string normalizedSystemId, short alterId)
    {
        var query = new SimpleStatement(
            $"SELECT id FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
            normalizedSystemId,
            alterId
        );

        var rows = await session.ExecuteAsync(query);
        return rows.Any();
    }

    private async Task<IReadOnlyList<SettingsFieldReadModel>> ResolveVisibleDefinitionsAsync(
        string systemId,
        string? friendshipLevel,
        CancellationToken cancellationToken)
    {
        var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);
        return definitions
            .Where(def => CanView(friendshipLevel, ParseVisibilityLevel(def.SecurityLevel)))
            .ToArray();
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

    private static IReadOnlyList<AlterPublicFieldReadModel> ResolveGuardedFields(
        IEnumerable<AlterFieldUdt>? alterFields,
        IReadOnlyList<SettingsFieldReadModel> definitions)
    {
        var valuesByFieldId = (alterFields ?? Array.Empty<AlterFieldUdt>())
            .ToDictionary(x => x.Id.ToString("N"), x => x.Value, StringComparer.OrdinalIgnoreCase);

        if (valuesByFieldId.Count == 0 || definitions.Count == 0)
        {
            return [];
        }

        return definitions
            .Where(def => valuesByFieldId.ContainsKey(def.Id))
            .Select(def => new AlterPublicFieldReadModel(def.Id, def.Name, def.Type, valuesByFieldId[def.Id]))
            .ToArray();
    }

    private static VisibilityLevel ParseVisibilityLevel(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "friends_only" => VisibilityLevel.FriendsOnly,
            "trusted_only" => VisibilityLevel.TrustedOnly,
            "private" => VisibilityLevel.Private,
            _ => VisibilityLevel.Public
        };
    }

    public static void EnsureAlterFieldUdtMapping(ISession session, string keyspace)
    {
        if (UdtMappings.ContainsKey(keyspace))
        {
            return;
        }

        session.UserDefinedTypes.Define(
            UdtMap.For<AlterFieldUdt>("alter_field", keyspace)
                .Map(f => f.Id, "id")
                .Map(f => f.Value, "value"));

        UdtMappings.TryAdd(keyspace, 0);
    }

    public sealed class AlterFieldUdt
    {
        public Guid Id { get; set; }
        public string? Value { get; set; }
    }
}
