using System.Collections.Concurrent;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryFriendshipRepository : IFriendshipRepository
{
    private sealed class FriendshipState
    {
        public required string FriendSystemId { get; init; }
        public required string Level { get; set; }
        public required DateTimeOffset Since { get; init; }
    }

    private sealed class RequestState
    {
        public required string OtherSystemId { get; init; }
        public required DateTimeOffset DateSent { get; init; }
    }

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, FriendshipState>> _friendships = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RequestState>> _outgoingRequests = new();

    public Task<string?> ResolveUserIdAsync(string userNameOrId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userNameOrId))
        {
            return Task.FromResult<string?>(null);
        }

        // In-memory mode has no user registry; treat provided value as canonical id,
        // but normalize by stripping any region prefix to match Scylla NormalizeSystemId semantics.
        return Task.FromResult<string?>(NormalizeSystemId(userNameOrId.Trim()));
    }

    public Task<string?> GetFriendshipLevelAsync(string systemId, string? viewerSystemId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(viewerSystemId))
        {
            return Task.FromResult<string?>(null);
        }

        var normalizedSystemId = NormalizeSystemId(systemId);
        var normalizedViewerId = NormalizeSystemId(viewerSystemId);

        if (string.Equals(normalizedSystemId, normalizedViewerId, StringComparison.Ordinal))
        {
            return Task.FromResult<string?>("trusted_friend");
        }

        if (!_friendships.TryGetValue(normalizedSystemId, out var store) || !store.TryGetValue(normalizedViewerId, out var state))
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(state.Level);
    }

    public Task<IReadOnlyList<FriendshipReadModel>> ListFriendshipsAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = NormalizeSystemId(systemId);
        if (!_friendships.TryGetValue(normalizedSystemId, out var store))
        {
            return Task.FromResult<IReadOnlyList<FriendshipReadModel>>(Array.Empty<FriendshipReadModel>());
        }

        var list = store.Values
            .OrderByDescending(x => x.Since)
            .Select(x => new FriendshipReadModel(
                new FriendProfileReadModel(x.FriendSystemId, null, null, null, null, null),
                new FriendshipModel(
                x.Level,
                x.Since),
                Array.Empty<FriendFrontingReadModel>()))
            .ToList();

        return Task.FromResult<IReadOnlyList<FriendshipReadModel>>(list);
    }

    public Task<FriendshipReadModel?> GetFriendshipAsync(string systemId, string friendSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = NormalizeSystemId(systemId);
        var normalizedFriendId = NormalizeSystemId(friendSystemId);

        if (!_friendships.TryGetValue(normalizedSystemId, out var store) || !store.TryGetValue(normalizedFriendId, out var state))
        {
            return Task.FromResult<FriendshipReadModel?>(null);
        }

        return Task.FromResult<FriendshipReadModel?>(new FriendshipReadModel(
            new FriendProfileReadModel(state.FriendSystemId, null, null, null, null, null),
            new FriendshipModel(
                state.Level,
                state.Since),
            Array.Empty<FriendFrontingReadModel>()));
    }

    public Task<bool> RemoveFriendshipAsync(string systemId, string friendSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = NormalizeSystemId(systemId);
        var normalizedFriendId = NormalizeSystemId(friendSystemId);

        if (!_friendships.TryGetValue(normalizedSystemId, out var userStore) || !userStore.TryRemove(normalizedFriendId, out _))
        {
            return Task.FromResult(false);
        }

        if (_friendships.TryGetValue(normalizedFriendId, out var peerStore))
        {
            peerStore.TryRemove(normalizedSystemId, out _);
        }

        return Task.FromResult(true);
    }

    public Task<bool> SetTrustedAsync(string systemId, string friendSystemId, bool trusted, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = NormalizeSystemId(systemId);
        var normalizedFriendId = NormalizeSystemId(friendSystemId);

        if (!_friendships.TryGetValue(normalizedSystemId, out var store) || !store.TryGetValue(normalizedFriendId, out var state))
        {
            return Task.FromResult(false);
        }

        state.Level = trusted ? "trusted_friend" : "friend";
        return Task.FromResult(true);
    }

    public Task<FriendRequestIndexReadModel> GetFriendRequestsAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = NormalizeSystemId(systemId);

        var outgoing = _outgoingRequests.TryGetValue(normalizedSystemId, out var outStore)
            ? outStore.Values
                .OrderByDescending(r => r.DateSent)
                .Select(r => new FriendRequestReadModel(
                    new FriendProfileReadModel(r.OtherSystemId, null, null, null, null, null),
                    new FriendshipRequestModel(r.DateSent)))
                .ToList()
            : new List<FriendRequestReadModel>();

        var incoming = _outgoingRequests
            .SelectMany(kvp => kvp.Value.Values.Select(r => (From: kvp.Key, Request: r)))
            .Where(x => x.Request.OtherSystemId == normalizedSystemId)
            .OrderByDescending(x => x.Request.DateSent)
            .Select(x => new FriendRequestReadModel(
                new FriendProfileReadModel(x.From, null, null, null, null, null),
                new FriendshipRequestModel(x.Request.DateSent)))
            .ToList();

        return Task.FromResult(new FriendRequestIndexReadModel(incoming, outgoing));
    }

    public Task<SendFriendRequestOutcome> SendRequestAsync(string systemId, string targetSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = NormalizeSystemId(systemId);
        var normalizedTargetId = NormalizeSystemId(targetSystemId);

        if (IsFriends(normalizedSystemId, normalizedTargetId))
        {
            return Task.FromResult(SendFriendRequestOutcome.AlreadyFriends);
        }

        if (HasOutgoingRequest(normalizedSystemId, normalizedTargetId))
        {
            return Task.FromResult(SendFriendRequestOutcome.AlreadySent);
        }

        if (HasOutgoingRequest(normalizedTargetId, normalizedSystemId))
        {
            LinkFriends(normalizedSystemId, normalizedTargetId);
            RemoveRequest(normalizedTargetId, normalizedSystemId);
            RemoveRequest(normalizedSystemId, normalizedTargetId);
            return Task.FromResult(SendFriendRequestOutcome.Accepted);
        }

        var store = _outgoingRequests.GetOrAdd(normalizedSystemId, _ => new ConcurrentDictionary<string, RequestState>());
        store[normalizedTargetId] = new RequestState
        {
            OtherSystemId = normalizedTargetId,
            DateSent = DateTimeOffset.UtcNow
        };

        return Task.FromResult(SendFriendRequestOutcome.Sent);
    }

    public Task<FriendRequestMutationOutcome> AcceptRequestAsync(string systemId, string sourceSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = NormalizeSystemId(systemId);
        var normalizedSourceId = NormalizeSystemId(sourceSystemId);

        if (IsFriends(normalizedSystemId, normalizedSourceId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.AlreadyFriends);
        }

        if (!HasOutgoingRequest(normalizedSourceId, normalizedSystemId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.NotRequested);
        }

        LinkFriends(normalizedSystemId, normalizedSourceId);
        RemoveRequest(normalizedSourceId, normalizedSystemId);
        RemoveRequest(normalizedSystemId, normalizedSourceId);
        return Task.FromResult(FriendRequestMutationOutcome.Ok);
    }

    public Task<FriendRequestMutationOutcome> RejectRequestAsync(string systemId, string sourceSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = NormalizeSystemId(systemId);
        var normalizedSourceId = NormalizeSystemId(sourceSystemId);

        if (IsFriends(normalizedSystemId, normalizedSourceId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.AlreadyFriends);
        }

        if (!HasOutgoingRequest(normalizedSourceId, normalizedSystemId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.NotRequested);
        }

        RemoveRequest(normalizedSourceId, normalizedSystemId);
        return Task.FromResult(FriendRequestMutationOutcome.Ok);
    }

    public Task<FriendRequestMutationOutcome> CancelRequestAsync(string systemId, string targetSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = NormalizeSystemId(systemId);
        var normalizedTargetId = NormalizeSystemId(targetSystemId);

        if (IsFriends(normalizedSystemId, normalizedTargetId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.AlreadyFriends);
        }

        if (!HasOutgoingRequest(normalizedSystemId, normalizedTargetId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.NotRequested);
        }

        RemoveRequest(normalizedSystemId, normalizedTargetId);
        return Task.FromResult(FriendRequestMutationOutcome.Ok);
    }

    public Task<IReadOnlyList<string>> DeleteAllForSystemAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = NormalizeSystemId(systemId);
        var friendIds = new List<string>();

        // Remove all friendships where this user is involved
        if (_friendships.TryRemove(normalizedSystemId, out var friends))
        {
            foreach (var friendId in friends.Keys)
            {
                friendIds.Add(friendId);
                if (_friendships.TryGetValue(friendId, out var peerStore))
                {
                    peerStore.TryRemove(normalizedSystemId, out _);
                }
            }
        }

        // Remove all outgoing requests from this user
        _outgoingRequests.TryRemove(normalizedSystemId, out _);

        // Remove all incoming requests to this user (we need to scan for this in-memory)
        foreach (var requesterId in _outgoingRequests.Keys)
        {
            if (_outgoingRequests.TryGetValue(requesterId, out var requests))
            {
                requests.TryRemove(normalizedSystemId, out _);
            }
        }

        return Task.FromResult((IReadOnlyList<string>)friendIds);
    }

    private bool IsFriends(string systemId, string friendSystemId)
        => _friendships.TryGetValue(systemId, out var store) && store.ContainsKey(friendSystemId);

    private bool HasOutgoingRequest(string fromSystemId, string toSystemId)
        => _outgoingRequests.TryGetValue(fromSystemId, out var store) && store.ContainsKey(toSystemId);

    private void RemoveRequest(string fromSystemId, string toSystemId)
    {
        if (_outgoingRequests.TryGetValue(fromSystemId, out var store))
        {
            store.TryRemove(toSystemId, out _);
        }
    }

    private void LinkFriends(string left, string right)
    {
        var now = DateTimeOffset.UtcNow;

        var leftStore = _friendships.GetOrAdd(left, _ => new ConcurrentDictionary<string, FriendshipState>());
        leftStore[right] = new FriendshipState
        {
            FriendSystemId = right,
            Level = "friend",
            Since = now
        };

        var rightStore = _friendships.GetOrAdd(right, _ => new ConcurrentDictionary<string, FriendshipState>());
        rightStore[left] = new FriendshipState
        {
            FriendSystemId = left,
            Level = "friend",
            Since = now
        };
    }

    private static string NormalizeSystemId(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return systemId;

        var separator = systemId.IndexOf(':');
        if (separator <= 0 || separator >= systemId.Length - 1)
            return systemId;

        return systemId[(separator + 1)..];
    }
}
