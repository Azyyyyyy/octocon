using Cassandra;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Friendships;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaFriendshipRepository : IFriendshipRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IRegionContext _regionContext;
    private readonly PersistenceRegistrationOptions _options;

    public ScyllaFriendshipRepository(
        IScyllaSessionProvider sessionProvider,
        IRegionContext regionContext,
        PersistenceRegistrationOptions options)
    {
        _sessionProvider = sessionProvider;
        _regionContext = regionContext;
        _options = options;
    }

    public async Task<IReadOnlyList<FriendshipReadModel>> ListFriendshipsAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT friend_id, level, since FROM friendships_by_system WHERE system_id = ?",
                scopedSystemId);

            var rows = await session.ExecuteAsync(query);
            var result = new List<FriendshipReadModel>();

            foreach (var row in rows)
            {
                var friendId = row.GetValue<string>("friend_id");
                var level = row.GetValue<string>("level");
                var since = row.GetValue<DateTimeOffset?>("since") ?? DateTimeOffset.UtcNow;

                var profile = await GetFriendProfileAsync(session, friendId);
                var fronting = await GetFrontingAsync(session, friendId);

                result.Add(new FriendshipReadModel(profile, level, since, fronting));
            }

            return result.OrderByDescending(x => x.Since).ToList();
        }, _options, cancellationToken);
    }

    public async Task<FriendshipReadModel?> GetFriendshipAsync(string systemId, string friendSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT friend_id, level, since FROM friendships_by_system WHERE system_id = ? AND friend_id = ? LIMIT 1",
                scopedSystemId,
                friendSystemId);

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is null)
            {
                return null;
            }

            var since = row.GetValue<DateTimeOffset?>("since") ?? DateTimeOffset.UtcNow;
            var profile = await GetFriendProfileAsync(session, friendSystemId);
            var fronting = await GetFrontingAsync(session, friendSystemId);

            return new FriendshipReadModel(
                profile,
                row.GetValue<string>("level"),
                since,
                fronting);
        }, _options, cancellationToken);
    }

    public async Task<bool> RemoveFriendshipAsync(string systemId, string friendSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);
            var scopedFriendId = GetScopedSystemId(friendSystemId);

            var exists = await ExistsFriendshipAsync(session, scopedSystemId, friendSystemId);
            if (!exists)
            {
                return false;
            }

            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM friendships_by_system WHERE system_id = ? AND friend_id = ?",
                scopedSystemId,
                friendSystemId));

            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM friendships_by_system WHERE system_id = ? AND friend_id = ?",
                scopedFriendId,
                systemId));

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> SetTrustedAsync(string systemId, string friendSystemId, bool trusted, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await ExistsFriendshipAsync(session, scopedSystemId, friendSystemId);
            if (!exists)
            {
                return false;
            }

            await session.ExecuteAsync(new SimpleStatement(
                "UPDATE friendships_by_system SET level = ? WHERE system_id = ? AND friend_id = ?",
                trusted ? "trusted_friend" : "friend",
                scopedSystemId,
                friendSystemId));

            return true;
        }, _options, cancellationToken);
    }

    public async Task<FriendRequestIndexReadModel> GetFriendRequestsAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var incomingRows = await session.ExecuteAsync(new SimpleStatement(
                "SELECT from_system_id, date_sent FROM friend_requests_by_recipient WHERE to_system_id = ?",
                scopedSystemId));

            var outgoingRows = await session.ExecuteAsync(new SimpleStatement(
                "SELECT to_system_id, date_sent FROM friend_requests_by_sender WHERE from_system_id = ?",
                scopedSystemId));

            var incoming = new List<FriendRequestReadModel>();
            foreach (var row in incomingRows)
            {
                var sourceSystemId = row.GetValue<string>("from_system_id");
                var profile = await GetFriendProfileAsync(session, sourceSystemId);
                incoming.Add(new FriendRequestReadModel(profile, row.GetValue<DateTimeOffset?>("date_sent") ?? DateTimeOffset.UtcNow));
            }

            var outgoing = new List<FriendRequestReadModel>();
            foreach (var row in outgoingRows)
            {
                var targetSystemId = row.GetValue<string>("to_system_id");
                var profile = await GetFriendProfileAsync(session, targetSystemId);
                outgoing.Add(new FriendRequestReadModel(profile, row.GetValue<DateTimeOffset?>("date_sent") ?? DateTimeOffset.UtcNow));
            }

            return new FriendRequestIndexReadModel(
                incoming.OrderByDescending(x => x.DateSent).ToList(),
                outgoing.OrderByDescending(x => x.DateSent).ToList());
        }, _options, cancellationToken);
    }

    public async Task<SendFriendRequestOutcome> SendRequestAsync(string systemId, string targetSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);
            var scopedTargetSystemId = GetScopedSystemId(targetSystemId);

            if (!await SystemExistsAsync(session, scopedTargetSystemId))
            {
                return SendFriendRequestOutcome.NoUser;
            }

            if (await ExistsFriendshipAsync(session, scopedSystemId, targetSystemId))
            {
                return SendFriendRequestOutcome.AlreadyFriends;
            }

            if (await ExistsRequestAsync(session, scopedSystemId, scopedTargetSystemId))
            {
                return SendFriendRequestOutcome.AlreadySent;
            }

            if (await ExistsRequestAsync(session, scopedTargetSystemId, scopedSystemId))
            {
                await LinkFriendsAsync(session, scopedSystemId, systemId, scopedTargetSystemId, targetSystemId);
                await DeleteRequestAsync(session, scopedTargetSystemId, scopedSystemId, targetSystemId, systemId);
                await DeleteRequestAsync(session, scopedSystemId, scopedTargetSystemId, systemId, targetSystemId);
                return SendFriendRequestOutcome.Accepted;
            }

            await CreateRequestAsync(session, scopedSystemId, scopedTargetSystemId, systemId, targetSystemId);
            return SendFriendRequestOutcome.Sent;
        }, _options, cancellationToken);
    }

    public async Task<FriendRequestMutationOutcome> AcceptRequestAsync(string systemId, string sourceSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);
            var scopedSourceSystemId = GetScopedSystemId(sourceSystemId);

            if (!await SystemExistsAsync(session, scopedSourceSystemId))
            {
                return FriendRequestMutationOutcome.NoUser;
            }

            if (await ExistsFriendshipAsync(session, scopedSystemId, sourceSystemId))
            {
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await ExistsRequestAsync(session, scopedSourceSystemId, scopedSystemId))
            {
                return FriendRequestMutationOutcome.NotRequested;
            }

            await LinkFriendsAsync(session, scopedSystemId, systemId, scopedSourceSystemId, sourceSystemId);
            await DeleteRequestAsync(session, scopedSourceSystemId, scopedSystemId, sourceSystemId, systemId);
            await DeleteRequestAsync(session, scopedSystemId, scopedSourceSystemId, systemId, sourceSystemId);
            return FriendRequestMutationOutcome.Ok;
        }, _options, cancellationToken);
    }

    public async Task<FriendRequestMutationOutcome> RejectRequestAsync(string systemId, string sourceSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);
            var scopedSourceSystemId = GetScopedSystemId(sourceSystemId);

            if (!await SystemExistsAsync(session, scopedSourceSystemId))
            {
                return FriendRequestMutationOutcome.NoUser;
            }

            if (await ExistsFriendshipAsync(session, scopedSystemId, sourceSystemId))
            {
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await ExistsRequestAsync(session, scopedSourceSystemId, scopedSystemId))
            {
                return FriendRequestMutationOutcome.NotRequested;
            }

            await DeleteRequestAsync(session, scopedSourceSystemId, scopedSystemId, sourceSystemId, systemId);
            return FriendRequestMutationOutcome.Ok;
        }, _options, cancellationToken);
    }

    public async Task<FriendRequestMutationOutcome> CancelRequestAsync(string systemId, string targetSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);
            var scopedTargetSystemId = GetScopedSystemId(targetSystemId);

            if (!await SystemExistsAsync(session, scopedTargetSystemId))
            {
                return FriendRequestMutationOutcome.NoUser;
            }

            if (await ExistsFriendshipAsync(session, scopedSystemId, targetSystemId))
            {
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await ExistsRequestAsync(session, scopedSystemId, scopedTargetSystemId))
            {
                return FriendRequestMutationOutcome.NotRequested;
            }

            await DeleteRequestAsync(session, scopedSystemId, scopedTargetSystemId, systemId, targetSystemId);
            return FriendRequestMutationOutcome.Ok;
        }, _options, cancellationToken);
    }

    private async Task<bool> ExistsFriendshipAsync(ISession session, string scopedSystemId, string friendSystemId)
    {
        var query = new SimpleStatement(
            "SELECT friend_id FROM friendships_by_system WHERE system_id = ? AND friend_id = ? LIMIT 1",
            scopedSystemId,
            friendSystemId);

        return (await session.ExecuteAsync(query)).Any();
    }

    private async Task<bool> SystemExistsAsync(ISession session, string scopedSystemId)
    {
        var query = new SimpleStatement(
            "SELECT system_id FROM account_profiles_by_system WHERE system_id = ? LIMIT 1",
            scopedSystemId);

        return (await session.ExecuteAsync(query)).Any();
    }

    private async Task<bool> ExistsRequestAsync(ISession session, string scopedFrom, string scopedTo)
    {
        var query = new SimpleStatement(
            "SELECT to_system_id FROM friend_requests_by_sender WHERE from_system_id = ? AND to_system_id = ? LIMIT 1",
            scopedFrom,
            scopedTo);

        return (await session.ExecuteAsync(query)).Any();
    }

    private async Task CreateRequestAsync(ISession session, string scopedFrom, string scopedTo, string fromSystemId, string toSystemId)
    {
        await session.ExecuteAsync(new SimpleStatement(
            "INSERT INTO friend_requests_by_sender (from_system_id, to_system_id, date_sent) VALUES (?, ?, toTimestamp(now()))",
            scopedFrom,
            scopedTo));

        await session.ExecuteAsync(new SimpleStatement(
            "INSERT INTO friend_requests_by_recipient (to_system_id, from_system_id, date_sent) VALUES (?, ?, toTimestamp(now()))",
            scopedTo,
            scopedFrom));
    }

    private async Task DeleteRequestAsync(ISession session, string scopedFrom, string scopedTo, string fromSystemId, string toSystemId)
    {
        await session.ExecuteAsync(new SimpleStatement(
            "DELETE FROM friend_requests_by_sender WHERE from_system_id = ? AND to_system_id = ?",
            scopedFrom,
            scopedTo));

        await session.ExecuteAsync(new SimpleStatement(
            "DELETE FROM friend_requests_by_recipient WHERE to_system_id = ? AND from_system_id = ?",
            scopedTo,
            scopedFrom));
    }

    private async Task LinkFriendsAsync(ISession session, string scopedLeft, string leftSystemId, string scopedRight, string rightSystemId)
    {
        await session.ExecuteAsync(new SimpleStatement(
            "INSERT INTO friendships_by_system (system_id, friend_id, level, since) VALUES (?, ?, ?, toTimestamp(now()))",
            scopedLeft,
            rightSystemId,
            "friend"));

        await session.ExecuteAsync(new SimpleStatement(
            "INSERT INTO friendships_by_system (system_id, friend_id, level, since) VALUES (?, ?, ?, toTimestamp(now()))",
            scopedRight,
            leftSystemId,
            "friend"));
    }

    private async Task<FriendProfileReadModel> GetFriendProfileAsync(ISession session, string friendSystemId)
    {
        var scopedFriendId = GetScopedSystemId(friendSystemId);
        var profileQuery = new SimpleStatement(
            "SELECT username FROM account_profiles_by_system WHERE system_id = ? LIMIT 1",
            scopedFriendId);

        var profileRow = (await session.ExecuteAsync(profileQuery)).FirstOrDefault();
        var username = profileRow is null ? null : profileRow.GetValue<string?>("username");

        return new FriendProfileReadModel(friendSystemId, username, null, null, null);
    }

    private async Task<IReadOnlyList<FriendFrontingReadModel>> GetFrontingAsync(ISession session, string friendSystemId)
    {
        var scopedFriendId = GetScopedSystemId(friendSystemId);

        var activeRows = await session.ExecuteAsync(new SimpleStatement(
            "SELECT alter_id, comment FROM fronting_active_by_system WHERE system_id = ?",
            scopedFriendId));

        var primaryRow = (await session.ExecuteAsync(new SimpleStatement(
            "SELECT alter_id FROM fronting_primary_by_system WHERE system_id = ? LIMIT 1",
            scopedFriendId))).FirstOrDefault();

        var primaryAlterId = primaryRow?.GetValue<int>("alter_id");

        var alterRows = await session.ExecuteAsync(new SimpleStatement(
            "SELECT alter_id, name, alias FROM alters_by_system WHERE system_id = ?",
            scopedFriendId));

        var alterMap = alterRows.ToDictionary(
            row => row.GetValue<int>("alter_id"),
            row => (Name: row.GetValue<string?>("name"), Alias: row.GetValue<string?>("alias")));

        return activeRows
            .Select(row =>
            {
                var alterId = row.GetValue<int>("alter_id");
                alterMap.TryGetValue(alterId, out var alter);
                return new FriendFrontingReadModel(
                    alterId,
                    alter.Name,
                    alter.Alias,
                    row.GetValue<string?>("comment"),
                    primaryAlterId == alterId);
            })
            .OrderBy(x => x.AlterId)
            .ToList();
    }

    private string GetScopedSystemId(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}
