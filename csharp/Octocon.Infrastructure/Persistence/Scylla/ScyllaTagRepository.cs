using Cassandra;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Tags;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaTagRepository : ITagRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IRegionContext _regionContext;
    private readonly PersistenceRegistrationOptions _options;

    public ScyllaTagRepository(
        IScyllaSessionProvider sessionProvider,
        IRegionContext regionContext,
        PersistenceRegistrationOptions options
    )
    {
        _sessionProvider = sessionProvider;
        _regionContext = regionContext;
        _options = options;
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
            var scopedSystemId = GetScopedSystemId(systemId);

            if (!string.IsNullOrWhiteSpace(command.ParentTagId))
            {
                var parentCheck = new SimpleStatement(
                    "SELECT tag_id FROM tags_by_system WHERE system_id = ? AND tag_id = ? LIMIT 1",
                    scopedSystemId,
                    command.ParentTagId
                );

                var parentRows = await session.ExecuteAsync(parentCheck);
                if (!parentRows.Any())
                    return null;
            }

            var tagId = Guid.NewGuid().ToString("N");

            var insert = new SimpleStatement(
                "INSERT INTO tags_by_system (system_id, tag_id, name, parent_tag_id) VALUES (?, ?, ?, ?)",
                scopedSystemId,
                tagId,
                command.Name,
                command.ParentTagId
            );

            await session.ExecuteAsync(insert);
            return tagId;
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
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT tag_id FROM tags_by_system WHERE system_id = ? AND tag_id = ? LIMIT 1",
                scopedSystemId,
                tagId
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
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await ExistsAsync(systemId, command.TagId, cancellationToken);
            if (!exists)
            {
                return false;
            }

            if (command.Name is not null)
            {
                var updateName = new SimpleStatement(
                    "UPDATE tags_by_system SET name = ? WHERE system_id = ? AND tag_id = ?",
                    command.Name,
                    scopedSystemId,
                    command.TagId
                );

                await session.ExecuteAsync(updateName);
            }

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string systemId, string tagId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await ExistsAsync(systemId, tagId, cancellationToken);
            if (!exists)
            {
                return false;
            }

            var deleteTag = new SimpleStatement(
                "DELETE FROM tags_by_system WHERE system_id = ? AND tag_id = ?",
                scopedSystemId,
                tagId
            );
            await session.ExecuteAsync(deleteTag);

            // Remove all tag memberships for this tag.
            var deleteMemberships = new SimpleStatement(
                "DELETE FROM tag_alters_by_system WHERE system_id = ? AND tag_id = ?",
                scopedSystemId,
                tagId
            );
            await session.ExecuteAsync(deleteMemberships);

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
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var tagExists = await ExistsAsync(systemId, tagId, cancellationToken);
            if (!tagExists)
            {
                return false;
            }

            var insert = new SimpleStatement(
                "INSERT INTO tag_alters_by_system (system_id, tag_id, alter_id) VALUES (?, ?, ?)",
                scopedSystemId,
                tagId,
                alterId
            );
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
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var edgeExistsQuery = new SimpleStatement(
                "SELECT alter_id FROM tag_alters_by_system WHERE system_id = ? AND tag_id = ? AND alter_id = ? LIMIT 1",
                scopedSystemId,
                tagId,
                alterId
            );

            var edgeRows = await session.ExecuteAsync(edgeExistsQuery);
            if (!edgeRows.Any())
            {
                return false;
            }

            var delete = new SimpleStatement(
                "DELETE FROM tag_alters_by_system WHERE system_id = ? AND tag_id = ? AND alter_id = ?",
                scopedSystemId,
                tagId,
                alterId
            );
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
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT parent_tag_id FROM tags_by_system WHERE system_id = ? AND tag_id = ? LIMIT 1",
                scopedSystemId,
                tagId
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null ? null : row.GetValue<string?>("parent_tag_id");
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
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var childExists = await ExistsAsync(systemId, tagId, cancellationToken);
            var parentExists = await ExistsAsync(systemId, parentTagId, cancellationToken);
            if (!childExists || !parentExists)
            {
                return false;
            }

            var update = new SimpleStatement(
                "UPDATE tags_by_system SET parent_tag_id = ? WHERE system_id = ? AND tag_id = ?",
                parentTagId,
                scopedSystemId,
                tagId
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
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await ExistsAsync(systemId, tagId, cancellationToken);
            if (!exists)
            {
                return false;
            }

            var update = new SimpleStatement(
                "UPDATE tags_by_system SET parent_tag_id = ? WHERE system_id = ? AND tag_id = ?",
                null,
                scopedSystemId,
                tagId
            );
            await session.ExecuteAsync(update);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<IReadOnlyList<TagPublicReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT tag_id, name, parent_tag_id FROM tags_by_system WHERE system_id = ?",
                scopedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            var tags = new List<TagPublicReadModel>();

            foreach (var row in rows)
            {
                var tagId = row.GetValue<string>("tag_id");
                var alterIds = await GetAlterIdsAsync(session, scopedSystemId, tagId);
                tags.Add(new TagPublicReadModel(
                    tagId,
                    row.GetValue<string>("name"),
                    row.GetValue<string?>("parent_tag_id"),
                    alterIds));
            }

            return tags.OrderBy(x => x.TagId).ToArray();
        }, _options, cancellationToken);
    }

    public async Task<TagPublicReadModel?> GetAsync(string systemId, string tagId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT tag_id, name, parent_tag_id FROM tags_by_system WHERE system_id = ? AND tag_id = ? LIMIT 1",
                scopedSystemId,
                tagId
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is null)
            {
                return null;
            }

            var alterIds = await GetAlterIdsAsync(session, scopedSystemId, tagId);
            return new TagPublicReadModel(
                row.GetValue<string>("tag_id"),
                row.GetValue<string>("name"),
                row.GetValue<string?>("parent_tag_id"),
                alterIds);
        }, _options, cancellationToken);
    }

    private static async Task<IReadOnlyList<int>> GetAlterIdsAsync(ISession session, string scopedSystemId, string tagId)
    {
        var query = new SimpleStatement(
            "SELECT alter_id FROM tag_alters_by_system WHERE system_id = ? AND tag_id = ?",
            scopedSystemId,
            tagId
        );

        var rows = await session.ExecuteAsync(query);
        return rows.Select(x => x.GetValue<int>("alter_id")).OrderBy(x => x).ToArray();
    }

    private string GetScopedSystemId(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}
