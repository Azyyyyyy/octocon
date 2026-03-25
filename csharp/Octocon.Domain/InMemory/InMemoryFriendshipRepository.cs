using System.Collections.Concurrent;
using Octocon.Domain.Friendships;

namespace Octocon.Domain.InMemory;

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

    public Task<string?> GetFriendshipLevelAsync(string systemId, string? viewerSystemId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(viewerSystemId))
        {
            return Task.FromResult<string?>(null);
        }

        if (string.Equals(systemId, viewerSystemId, StringComparison.Ordinal))
        {
            return Task.FromResult<string?>("trusted_friend");
        }

        if (!_friendships.TryGetValue(systemId, out var store) || !store.TryGetValue(viewerSystemId, out var state))
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(state.Level);
    }

    public Task<IReadOnlyList<FriendshipReadModel>> ListFriendshipsAsync(string systemId, CancellationToken cancellationToken = default)
    {
        if (!_friendships.TryGetValue(systemId, out var store))
        {
            return Task.FromResult<IReadOnlyList<FriendshipReadModel>>(Array.Empty<FriendshipReadModel>());
        }

        var list = store.Values
            .OrderByDescending(x => x.Since)
            .Select(x => new FriendshipReadModel(
                new FriendProfileReadModel(x.FriendSystemId, null, null, null, null),
                new FriendshipModel(
                x.Level,
                x.Since),
                Array.Empty<FriendFrontingReadModel>()))
            .ToList();

        return Task.FromResult<IReadOnlyList<FriendshipReadModel>>(list);
    }

    public Task<FriendshipReadModel?> GetFriendshipAsync(string systemId, string friendSystemId, CancellationToken cancellationToken = default)
    {
        if (!_friendships.TryGetValue(systemId, out var store) || !store.TryGetValue(friendSystemId, out var state))
        {
            return Task.FromResult<FriendshipReadModel?>(null);
        }

        return Task.FromResult<FriendshipReadModel?>(new FriendshipReadModel(
            new FriendProfileReadModel(friendSystemId, null, null, null, null),
            new FriendshipModel(
                state.Level,
                state.Since),
            Array.Empty<FriendFrontingReadModel>()));
    }

    public Task<bool> RemoveFriendshipAsync(string systemId, string friendSystemId, CancellationToken cancellationToken = default)
    {
        if (!_friendships.TryGetValue(systemId, out var userStore) || !userStore.TryRemove(friendSystemId, out _))
        {
            return Task.FromResult(false);
        }

        if (_friendships.TryGetValue(friendSystemId, out var peerStore))
        {
            peerStore.TryRemove(systemId, out _);
        }

        return Task.FromResult(true);
    }

    public Task<bool> SetTrustedAsync(string systemId, string friendSystemId, bool trusted, CancellationToken cancellationToken = default)
    {
        if (!_friendships.TryGetValue(systemId, out var store) || !store.TryGetValue(friendSystemId, out var state))
        {
            return Task.FromResult(false);
        }

        state.Level = trusted ? "trusted_friend" : "friend";
        return Task.FromResult(true);
    }

    public Task<FriendRequestIndexReadModel> GetFriendRequestsAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var outgoing = _outgoingRequests.TryGetValue(systemId, out var outStore)
            ? outStore.Values
                .OrderByDescending(r => r.DateSent)
                .Select(r => new FriendRequestReadModel(
                    new FriendProfileReadModel(r.OtherSystemId, null, null, null, null),
                    new FriendshipRequestModel(r.DateSent)))
                .ToList()
            : new List<FriendRequestReadModel>();

        var incoming = _outgoingRequests
            .SelectMany(kvp => kvp.Value.Values.Select(r => (From: kvp.Key, Request: r)))
            .Where(x => x.Request.OtherSystemId == systemId)
            .OrderByDescending(x => x.Request.DateSent)
            .Select(x => new FriendRequestReadModel(
                new FriendProfileReadModel(x.From, null, null, null, null),
                new FriendshipRequestModel(x.Request.DateSent)))
            .ToList();

        return Task.FromResult(new FriendRequestIndexReadModel(incoming, outgoing));
    }

    public Task<SendFriendRequestOutcome> SendRequestAsync(string systemId, string targetSystemId, CancellationToken cancellationToken = default)
    {
        if (IsFriends(systemId, targetSystemId))
        {
            return Task.FromResult(SendFriendRequestOutcome.AlreadyFriends);
        }

        if (HasOutgoingRequest(systemId, targetSystemId))
        {
            return Task.FromResult(SendFriendRequestOutcome.AlreadySent);
        }

        if (HasOutgoingRequest(targetSystemId, systemId))
        {
            LinkFriends(systemId, targetSystemId);
            RemoveRequest(targetSystemId, systemId);
            RemoveRequest(systemId, targetSystemId);
            return Task.FromResult(SendFriendRequestOutcome.Accepted);
        }

        var store = _outgoingRequests.GetOrAdd(systemId, _ => new ConcurrentDictionary<string, RequestState>());
        store[targetSystemId] = new RequestState
        {
            OtherSystemId = targetSystemId,
            DateSent = DateTimeOffset.UtcNow
        };

        return Task.FromResult(SendFriendRequestOutcome.Sent);
    }

    public Task<FriendRequestMutationOutcome> AcceptRequestAsync(string systemId, string sourceSystemId, CancellationToken cancellationToken = default)
    {
        if (IsFriends(systemId, sourceSystemId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.AlreadyFriends);
        }

        if (!HasOutgoingRequest(sourceSystemId, systemId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.NotRequested);
        }

        LinkFriends(systemId, sourceSystemId);
        RemoveRequest(sourceSystemId, systemId);
        RemoveRequest(systemId, sourceSystemId);
        return Task.FromResult(FriendRequestMutationOutcome.Ok);
    }

    public Task<FriendRequestMutationOutcome> RejectRequestAsync(string systemId, string sourceSystemId, CancellationToken cancellationToken = default)
    {
        if (IsFriends(systemId, sourceSystemId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.AlreadyFriends);
        }

        if (!HasOutgoingRequest(sourceSystemId, systemId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.NotRequested);
        }

        RemoveRequest(sourceSystemId, systemId);
        return Task.FromResult(FriendRequestMutationOutcome.Ok);
    }

    public Task<FriendRequestMutationOutcome> CancelRequestAsync(string systemId, string targetSystemId, CancellationToken cancellationToken = default)
    {
        if (IsFriends(systemId, targetSystemId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.AlreadyFriends);
        }

        if (!HasOutgoingRequest(systemId, targetSystemId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.NotRequested);
        }

        RemoveRequest(systemId, targetSystemId);
        return Task.FromResult(FriendRequestMutationOutcome.Ok);
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
}
