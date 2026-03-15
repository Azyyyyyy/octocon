using Cassandra;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Polls;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaPollRepository : IPollRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IRegionContext _regionContext;
    private readonly PersistenceRegistrationOptions _options;

    public ScyllaPollRepository(
        IScyllaSessionProvider sessionProvider,
        IRegionContext regionContext,
        PersistenceRegistrationOptions options)
    {
        _sessionProvider = sessionProvider;
        _regionContext = regionContext;
        _options = options;
    }

    public async Task<IReadOnlyList<PollReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT poll_id, title, description, poll_type, data_json, time_end FROM polls_by_system WHERE system_id = ?",
                scopedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Select(ToReadModel).OrderBy(p => p.PollId, StringComparer.Ordinal).ToList();
        }, _options, cancellationToken);
    }

    public async Task<PollReadModel?> GetAsync(string systemId, string pollId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT poll_id, title, description, poll_type, data_json, time_end FROM polls_by_system WHERE system_id = ? AND poll_id = ? LIMIT 1",
                scopedSystemId,
                pollId
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null ? null : ToReadModel(row);
        }, _options, cancellationToken);
    }

    public async Task<string?> CreateAsync(string systemId, CreatePollCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);
            var pollId = Guid.NewGuid().ToString("N");

            var insert = new SimpleStatement(
                "INSERT INTO polls_by_system (system_id, poll_id, title, description, poll_type, data_json, time_end) VALUES (?, ?, ?, ?, ?, ?, ?)",
                scopedSystemId,
                pollId,
                command.Title,
                command.Description,
                command.Type,
                "{}",
                ParseTime(command.TimeEndIso)
            );

            await session.ExecuteAsync(insert);
            return pollId;
        }, _options, cancellationToken);
    }

    public async Task<bool> ExistsAsync(string systemId, string pollId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT poll_id FROM polls_by_system WHERE system_id = ? AND poll_id = ? LIMIT 1",
                scopedSystemId,
                pollId
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any();
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateAsync(string systemId, UpdatePollCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await ExistsAsync(systemId, command.PollId, cancellationToken);
            if (!exists)
                return false;

            if (command.Title is not null)
            {
                var q = new SimpleStatement(
                    "UPDATE polls_by_system SET title = ? WHERE system_id = ? AND poll_id = ?",
                    command.Title,
                    scopedSystemId,
                    command.PollId
                );
                await session.ExecuteAsync(q);
            }

            if (command.Description is not null)
            {
                var q = new SimpleStatement(
                    "UPDATE polls_by_system SET description = ? WHERE system_id = ? AND poll_id = ?",
                    command.Description,
                    scopedSystemId,
                    command.PollId
                );
                await session.ExecuteAsync(q);
            }

            if (command.TimeEndIso is not null)
            {
                var q = new SimpleStatement(
                    "UPDATE polls_by_system SET time_end = ? WHERE system_id = ? AND poll_id = ?",
                    ParseTime(command.TimeEndIso),
                    scopedSystemId,
                    command.PollId
                );
                await session.ExecuteAsync(q);
            }

            if (command.DataJson is not null)
            {
                var q = new SimpleStatement(
                    "UPDATE polls_by_system SET data_json = ? WHERE system_id = ? AND poll_id = ?",
                    command.DataJson,
                    scopedSystemId,
                    command.PollId
                );
                await session.ExecuteAsync(q);
            }

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string systemId, string pollId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await ExistsAsync(systemId, pollId, cancellationToken);
            if (!exists)
                return false;

            var delete = new SimpleStatement(
                "DELETE FROM polls_by_system WHERE system_id = ? AND poll_id = ?",
                scopedSystemId,
                pollId
            );
            await session.ExecuteAsync(delete);
            return true;
        }, _options, cancellationToken);
    }

    private string GetScopedSystemId(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }

    private static DateTimeOffset? ParseTime(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        return DateTimeOffset.TryParse(iso, out var value) ? value : null;
    }

    private static PollReadModel ToReadModel(Row row)
        => new(
            row.GetValue<string>("poll_id"),
            row.GetValue<string>("title"),
            row.GetValue<string?>("description"),
            row.GetValue<string>("poll_type"),
            row.GetValue<string?>("data_json") ?? "{}",
            row.GetValue<DateTimeOffset?>("time_end")
        );
}
