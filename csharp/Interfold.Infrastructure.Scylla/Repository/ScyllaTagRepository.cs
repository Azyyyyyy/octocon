using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaTagRepository : ITagRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;
    private readonly IAlterRepository _alterRepository;

    public ScyllaTagRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceConfiguration options,
        IAlterRepository alterRepository
    )
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
        _alterRepository = alterRepository;
    }

    public async Task<string?> CreateAsync(
        string systemId,
        CreateTagCommand command,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            Guid? parentTagId = null;

            if (!string.IsNullOrWhiteSpace(command.ParentTagId))
            {
                if (!TryParseUuid(command.ParentTagId, out var parentTagGuid))
                {
                    return null;
                }

                parentTagId = parentTagGuid;
            }

            if (!string.IsNullOrWhiteSpace(command.ParentTagId))
            {
                var parentCheck = new SimpleStatement(
                    $"SELECT id FROM {keyspace}.tags WHERE user_id = ? AND id = ? LIMIT 1",
                    normalizedSystemId,
                    parentTagId
                );

                var parentRows = await session.ExecuteAsync(parentCheck);
                if (!parentRows.Any())
                    return null;
            }

            var tagGuid = Guid.NewGuid();

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.tags (user_id, id, parent_tag_id, name, description, color, security_level, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                normalizedSystemId,
                tagGuid,
                parentTagId,
                command.Name,
                null,
                null,
                null
            );

            await session.ExecuteAsync(insert);
            return tagGuid.ToString("N");
        }, _options, cancellationToken);
    }

    public async Task<bool> ExistsAsync(
        string systemId,
        string tagId,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(tagId, out var tagGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id FROM {keyspace}.tags WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                tagGuid
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any();
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateAsync(
        string systemId,
        UpdateTagCommand command,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            if (!TryParseUuid(command.TagId, out var tagGuid))
            {
                return false;
            }

            var exists = await ExistsAsync(systemId, command.TagId, cancellationToken);
            if (!exists)
            {
                return false;
            }

            var setClauses = new List<string>();
            var values = new List<object?>();

            if (command.Name is not null)
            {
                setClauses.Add("name = ?");
                values.Add(command.Name);
            }

            if (command.Color is not null)
            {
                setClauses.Add("color = ?");
                values.Add(command.Color);
            }

            if (command.Description is not null)
            {
                setClauses.Add("description = ?");
                values.Add(command.Description);
            }

            if (command.SecurityLevel is not null)
            {
                setClauses.Add("security_level = ?");
                values.Add(ToSecurityLevel(command.SecurityLevel));
            }

            if (setClauses.Count == 0)
            {
                return true;
            }

            setClauses.Add("updated_at = toTimestamp(now())");
            values.Add(normalizedSystemId);
            values.Add(tagGuid);

            var update = new SimpleStatement(
                $"UPDATE {keyspace}.tags SET {string.Join(", ", setClauses)} WHERE user_id = ? AND id = ?",
                [.. values]);

            await session.ExecuteAsync(update);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string systemId, string tagId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(tagId, out var tagGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsAsync(systemId, tagId, cancellationToken);
            if (!exists)
            {
                return false;
            }

            var deleteBatch = new BatchStatement();
            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.tags WHERE user_id = ? AND id = ?",
                normalizedSystemId,
                tagGuid
            ));
            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.alter_tags WHERE user_id = ? AND tag_id = ?",
                normalizedSystemId,
                tagGuid
            ));

            // Clean up alter_tags_by_alter: find all alters with this tag and remove reverse entries
            var tagAlters = await session.ExecuteAsync(new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.alter_tags WHERE user_id = ? AND tag_id = ?",
                normalizedSystemId, tagGuid));
            foreach (var tagAlterRow in tagAlters)
            {
                var aid = tagAlterRow.GetValue<short>("alter_id");
                deleteBatch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.alter_tags_by_alter WHERE user_id = ? AND alter_id = ? AND tag_id = ?",
                    normalizedSystemId, aid, tagGuid));
            }

            await session.ExecuteAsync(deleteBatch);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> AttachAlterAsync(
        string systemId,
        string tagId,
        int alterId,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(tagId, out var tagGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var tagExists = await ExistsAsync(systemId, tagId, cancellationToken);
            if (!tagExists)
            {
                return false;
            }

            var insert = new BatchStatement();
            insert.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.alter_tags (user_id, tag_id, alter_id, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                normalizedSystemId,
                tagGuid,
                (short)alterId
            ));
            insert.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.alter_tags_by_alter (user_id, alter_id, tag_id, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                normalizedSystemId,
                (short)alterId,
                tagGuid
            ));
            await session.ExecuteAsync(insert);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> DetachAlterAsync(
        string systemId,
        string tagId,
        int alterId,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(tagId, out var tagGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var edgeExistsQuery = new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.alter_tags WHERE user_id = ? AND tag_id = ? AND alter_id = ? LIMIT 1",
                normalizedSystemId,
                tagGuid,
                (short)alterId
            );

            var edgeRows = await session.ExecuteAsync(edgeExistsQuery);
            if (!edgeRows.Any())
            {
                return false;
            }

            var delete = new BatchStatement();
            delete.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.alter_tags WHERE user_id = ? AND tag_id = ? AND alter_id = ?",
                normalizedSystemId,
                tagGuid,
                (short)alterId
            ));
            delete.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.alter_tags_by_alter WHERE user_id = ? AND alter_id = ? AND tag_id = ?",
                normalizedSystemId,
                (short)alterId,
                tagGuid
            ));
            await session.ExecuteAsync(delete);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<string?> GetParentIdAsync(
        string systemId,
        string tagId,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(tagId, out var tagGuid))
            {
                return null;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT parent_tag_id FROM {keyspace}.tags WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                tagGuid
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null ? null : row.GetValue<Guid?>("parent_tag_id")?.ToString("N");
        }, _options, cancellationToken);
    }

    public async Task<bool> SetParentAsync(
        string systemId,
        string tagId,
        string parentTagId,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(tagId, out var tagGuid) || !TryParseUuid(parentTagId, out var parentTagGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var childExists = await ExistsAsync(systemId, tagId, cancellationToken);
            var parentExists = await ExistsAsync(systemId, parentTagId, cancellationToken);
            if (!childExists || !parentExists)
            {
                return false;
            }

            var update = new SimpleStatement(
                $"UPDATE {keyspace}.tags SET parent_tag_id = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                parentTagGuid,
                normalizedSystemId,
                tagGuid
            );
            await session.ExecuteAsync(update);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> RemoveParentAsync(
        string systemId,
        string tagId,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(tagId, out var tagGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsAsync(systemId, tagId, cancellationToken);
            if (!exists)
            {
                return false;
            }

            var update = new SimpleStatement(
                $"UPDATE {keyspace}.tags SET parent_tag_id = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                null,
                normalizedSystemId,
                tagGuid
            );
            await session.ExecuteAsync(update);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<IReadOnlyList<TagReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id, name, color, description, parent_tag_id, inserted_at, updated_at, security_level, user_id FROM {keyspace}.tags WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            var tags = new List<TagReadModel>();

            foreach (var row in rows)
            {
                var tagId = row.GetValue<Guid>("id").ToString("N");
                var alterIds = await GetAlterIdsAsync(session, keyspace, normalizedSystemId, row.GetValue<Guid>("id"));
                tags.Add(new TagReadModel(
                    tagId,
                    row.GetValue<string>("name"),
                    row.GetValue<string?>("color"),
                    row.GetValue<string?>("description"),
                    row.GetValue<Guid?>("parent_tag_id")?.ToString("N"),
                    alterIds,
                    row.GetValue<DateTimeOffset>("inserted_at").UtcDateTime,
                    row.GetValue<DateTimeOffset>("updated_at").UtcDateTime,
                    ResolveVisibilityLevel(row.GetValue<short?>("security_level")),
                    row.GetValue<string>("user_id")));
            }

            // VERIFIED: 2026-03-17 Elixir tags.ex get_tags() has no explicit sort → database order (ascending). Matches C# OrderBy.
            return tags.OrderBy(x => x.Id).ToArray();
        }, _options, cancellationToken);
    }

    public async Task<IReadOnlyList<TagPublicReadModel>> ListGuardedAsync(
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
            var hydrationConcurrency = _options.HydrationMaxConcurrency;

            var query = new SimpleStatement(
                $"SELECT id, name, color, description, parent_tag_id, inserted_at, updated_at, security_level, user_id FROM {keyspace}.tags WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            var tags = new List<TagPublicReadModel>();

            foreach (var row in rows)
            {
                var visibility = ResolveVisibilityLevel(row.GetValue<short?>("security_level"));
                if (!CanView(friendshipLevel, visibility))
                {
                    continue;
                }

                var tagId = row.GetValue<Guid>("id").ToString("N");
                var alterIds = await GetGuardedAlterIdsAsync(session, keyspace, normalizedSystemId, row.GetValue<Guid>("id"), friendshipLevel);
                var alters = alterIds.Count == 0
                    ? Array.Empty<BareAlter>()
                    : (await ConcurrentProjection.SelectWithConcurrencyAsync(
                            alterIds,
                            hydrationConcurrency,
                            id => _alterRepository.GetGuardedAsync(systemId, id, viewerSystemId, cancellationToken),
                            cancellationToken))
                        .Where(x => x != null)
                        .ToArray();
                tags.Add(new TagPublicReadModel(
                    tagId,
                    row.GetValue<string>("name"),
                    row.GetValue<string?>("color"),
                    row.GetValue<string?>("description"),
                    row.GetValue<Guid?>("parent_tag_id")?.ToString("N"),
                    alters!,
                    row.GetValue<DateTimeOffset>("inserted_at").UtcDateTime,
                    row.GetValue<DateTimeOffset>("updated_at").UtcDateTime,
                    visibility,
                    row.GetValue<string>("user_id")));
            }

            return tags.OrderBy(x => x.Id).ToArray();
        }, _options, cancellationToken);
    }

    public async Task<TagReadModel?> GetAsync(string systemId, string tagId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(tagId, out var tagGuid))
            {
                return null;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id, name, color, description, parent_tag_id, inserted_at, updated_at, security_level, user_id FROM {keyspace}.tags WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                tagGuid
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is null)
            {
                return null;
            }

            var alterIds = await GetAlterIdsAsync(session, keyspace, normalizedSystemId, tagGuid);           
            return new TagReadModel(
                row.GetValue<Guid>("id").ToString("N"),
                row.GetValue<string>("name"),
                row.GetValue<string?>("color"),
                row.GetValue<string?>("description"),
                row.GetValue<Guid?>("parent_tag_id")?.ToString("N"),
                alterIds,
                row.GetValue<DateTimeOffset>("inserted_at").UtcDateTime,
                row.GetValue<DateTimeOffset>("updated_at").UtcDateTime,
                ResolveVisibilityLevel(row.GetValue<short?>("security_level")),
                row.GetValue<string>("user_id"));
        }, _options, cancellationToken);
    }

    public async Task<TagPublicReadModel?> GetGuardedAsync(
        string systemId,
        string tagId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(tagId, out var tagGuid))
            {
                return null;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var friendshipLevel = await ResolveFriendshipLevelAsync(session, normalizedSystemId, viewerSystemId);
            var hydrationConcurrency = _options.HydrationMaxConcurrency;

            var query = new SimpleStatement(
                $"SELECT id, name, color, description, parent_tag_id, inserted_at, updated_at, security_level, user_id FROM {keyspace}.tags WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                tagGuid
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is null)
            {
                return null;
            }

            var visibility = ResolveVisibilityLevel(row.GetValue<short?>("security_level"));
            if (!CanView(friendshipLevel, visibility))
            {
                return null;
            }

            var alterIds = await GetGuardedAlterIdsAsync(session, keyspace, normalizedSystemId, tagGuid, friendshipLevel);
            var alters = alterIds.Count == 0
                ? Array.Empty<BareAlter>()
                : (await ConcurrentProjection.SelectWithConcurrencyAsync(
                        alterIds,
                        hydrationConcurrency,
                        id => _alterRepository.GetGuardedAsync(systemId, id, viewerSystemId, cancellationToken),
                        cancellationToken))
                    .Where(x => x != null)
                    .ToArray();
            return new TagPublicReadModel(
                row.GetValue<Guid>("id").ToString("N"),
                row.GetValue<string>("name"),
                row.GetValue<string?>("color"),
                row.GetValue<string?>("description"),
                row.GetValue<Guid?>("parent_tag_id")?.ToString("N"),
                alters!,
                row.GetValue<DateTimeOffset>("inserted_at").UtcDateTime,
                row.GetValue<DateTimeOffset>("updated_at").UtcDateTime,
                visibility,
                row.GetValue<string>("user_id"));
        }, _options, cancellationToken);
    }

    private static async Task<IReadOnlyList<int>> GetAlterIdsAsync(ISession session, string keyspace, string normalizedSystemId, Guid tagId)
    {
        var query = new SimpleStatement(
            $"SELECT alter_id FROM {keyspace}.alter_tags WHERE user_id = ? AND tag_id = ?",
            normalizedSystemId,
            tagId
        );

        var rows = await session.ExecuteAsync(query);
        return rows.Select(x => (int)x.GetValue<short>("alter_id")).OrderBy(x => x).ToArray();
    }

    private static async Task<IReadOnlyList<int>> GetGuardedAlterIdsAsync(
        ISession session,
        string keyspace,
        string normalizedSystemId,
        Guid tagId,
        string? friendshipLevel)
    {
        var alterIds = await GetAlterIdsAsync(session, keyspace, normalizedSystemId, tagId);
        if (alterIds.Count == 0)
        {
            return alterIds;
        }

        var query = new SimpleStatement(
            $"SELECT id, security_level FROM {keyspace}.alters WHERE user_id = ?",
            normalizedSystemId);

        var rows = await session.ExecuteAsync(query);
        var visible = rows
            .Select(row => new
            {
                AlterId = (int)row.GetValue<short>("id"),
                Visibility = ResolveVisibilityLevel(row.GetValue<short?>("security_level"))
            })
            .Where(x => alterIds.Contains(x.AlterId) && CanView(friendshipLevel, x.Visibility))
            .Select(x => x.AlterId)
            .OrderBy(x => x)
            .ToArray();

        return visible;
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

    internal static bool TryParseUuid(string value, out Guid guid)
    {
        if (Guid.TryParseExact(value, "N", out guid))
        {
            return true;
        }

        return Guid.TryParse(value, out guid);
    }
}
