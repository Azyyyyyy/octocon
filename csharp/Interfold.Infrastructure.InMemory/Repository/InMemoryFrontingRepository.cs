using System.Collections.Concurrent;
using Interfold.Domain.Alters;
using Interfold.Domain.Friendships;
using Interfold.Domain.Fronting;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryFrontingRepository : IFrontingRepository
{
    private sealed class FrontState
    {
        public required string FrontId { get; init; }
        public required int AlterId { get; init; }
        public string? Comment { get; set; }
        public required DateTimeOffset StartedAt { get; init; }
    }

    private sealed class FrontHistoryState
    {
        public required string FrontId { get; init; }
        public required int AlterId { get; init; }
        public string? Comment { get; set; }
        public required DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; set; }
    }

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, FrontState>> _activeBySystem = new();
    private readonly ConcurrentDictionary<string, List<FrontHistoryState>> _historyBySystem = new();
    private readonly ConcurrentDictionary<string, int?> _primaryBySystem = new();
    private readonly object _sync = new();
    private readonly IFriendshipRepository? _friendships;

    public InMemoryFrontingRepository()
    {
    }

    public InMemoryFrontingRepository(IFriendshipRepository friendships)
    {
        _friendships = friendships;
    }

    public Task<bool> IsFrontingAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var active = _activeBySystem.TryGetValue(systemId, out var set) && set.ContainsKey(alterId);
            return Task.FromResult(active);
        }
    }

    public Task<string?> StartAsync(
        string systemId,
        int alterId,
        string? comment,
        CancellationToken cancellationToken = default
    )
    {
        lock (_sync)
        {
            var set = _activeBySystem.GetOrAdd(systemId, _ => new ConcurrentDictionary<int, FrontState>());
            if (set.ContainsKey(alterId))
            {
                return Task.FromResult<string?>(null);
            }

            var frontId = Guid.NewGuid().ToString("N");
            set[alterId] = new FrontState
            {
                FrontId = frontId,
                AlterId = alterId,
                Comment = comment,
                StartedAt = DateTimeOffset.UtcNow
            };

            var history = _historyBySystem.GetOrAdd(systemId, _ => new List<FrontHistoryState>());
            history.Add(new FrontHistoryState
            {
                FrontId = frontId,
                AlterId = alterId,
                Comment = comment,
                StartedAt = set[alterId].StartedAt,
                EndedAt = null
            });

            return Task.FromResult<string?>(frontId);
        }
    }

    public Task<bool> EndAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_activeBySystem.TryGetValue(systemId, out var set))
            {
                return Task.FromResult(false);
            }

            var removed = set.TryRemove(alterId, out var removedFront);
            if (removed && _primaryBySystem.TryGetValue(systemId, out var currentPrimary) && currentPrimary == alterId)
            {
                _primaryBySystem[systemId] = null;
            }

            if (removed && removedFront is not null && _historyBySystem.TryGetValue(systemId, out var history))
            {
                var historical = history.LastOrDefault(
                    x => string.Equals(x.FrontId, removedFront.FrontId, StringComparison.Ordinal) &&
                         x.EndedAt is null);
                if (historical is not null)
                {
                    historical.EndedAt = DateTimeOffset.UtcNow;
                }
            }

            return Task.FromResult(removed);
        }
    }

    public Task<bool> SetPrimaryAsync(string systemId, int? alterId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (alterId is int value)
            {
                if (!_activeBySystem.TryGetValue(systemId, out var set) || !set.ContainsKey(value))
                {
                    return Task.FromResult(false);
                }
            }

            _primaryBySystem[systemId] = alterId;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<FrontActiveReadModel>> ListActiveAsync(string systemId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_activeBySystem.TryGetValue(systemId, out var set))
                return Task.FromResult<IReadOnlyList<FrontActiveReadModel>>(Array.Empty<FrontActiveReadModel>());

            _primaryBySystem.TryGetValue(systemId, out var primary);

            var results = set.Values
                .Select(x => new FrontActiveReadModel(
                    new BareAlter(x.AlterId, $"Alter {x.AlterId}", null, null, null, null, null!),
                    new FrontHistoryReadModel(x.FrontId, x.AlterId, x.Comment, x.StartedAt, null, systemId),
                    primary == x.AlterId))
                .OrderByDescending(x => x.Front.TimeStart)
                .ToArray();

            return Task.FromResult<IReadOnlyList<FrontActiveReadModel>>(results);
        }
    }

    public async Task<IReadOnlyList<FrontActiveReadModel>> ListActiveGuardedAsync(
        string systemId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var friendshipLevel = await ResolveFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
        if (!CanView(friendshipLevel, VisibilityLevel.Public))
        {
            return Array.Empty<FrontActiveReadModel>();
        }

        return await ListActiveAsync(systemId, cancellationToken);
    }

    public Task<IReadOnlyList<FrontHistoryReadModel>> ListHistoryBetweenAsync(
        string systemId,
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_historyBySystem.TryGetValue(systemId, out var history))
            {
                return Task.FromResult<IReadOnlyList<FrontHistoryReadModel>>(Array.Empty<FrontHistoryReadModel>());
            }

            var results = history
                .Where(x => x.StartedAt >= startInclusive && x.StartedAt <= endInclusive)
                .OrderByDescending(x => x.StartedAt)
                .Select(x => new FrontHistoryReadModel(x.FrontId, x.AlterId, x.Comment, x.StartedAt, x.EndedAt, systemId))
                .ToArray();

            return Task.FromResult<IReadOnlyList<FrontHistoryReadModel>>(results);
        }
    }

    public Task<FrontActiveReadModel?> GetActiveByFrontIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_activeBySystem.TryGetValue(systemId, out var set))
                return Task.FromResult<FrontActiveReadModel?>(null);

            _primaryBySystem.TryGetValue(systemId, out var primary);

            var found = set.Values.FirstOrDefault(x => string.Equals(x.FrontId, frontId, StringComparison.Ordinal));
            if (found is null)
                return Task.FromResult<FrontActiveReadModel?>(null);

            return Task.FromResult<FrontActiveReadModel?>(new FrontActiveReadModel(
                new BareAlter(found.AlterId, $"Alter {found.AlterId}", null, null, null, null, null!),
                new FrontHistoryReadModel(found.FrontId, found.AlterId, found.Comment, found.StartedAt, null, systemId),
                primary == found.AlterId));
        }
    }

    public Task<FrontHistoryReadModel?> GetHistoryEntryByFrontIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_historyBySystem.TryGetValue(systemId, out var history))
                return Task.FromResult<FrontHistoryReadModel?>(null);

            var entry = history.FirstOrDefault(x => string.Equals(x.FrontId, frontId, StringComparison.Ordinal));
            if (entry is null)
                return Task.FromResult<FrontHistoryReadModel?>(null);

            return Task.FromResult<FrontHistoryReadModel?>(new FrontHistoryReadModel(
                entry.FrontId, entry.AlterId, entry.Comment, entry.StartedAt, entry.EndedAt, systemId));
        }
    }

    public async Task<bool> EndByFrontIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default)
    {
        var found = await GetActiveByFrontIdAsync(systemId, frontId, cancellationToken);
        if (found is null)
            return false;

        return await EndAsync(systemId, found.Front.AlterId, cancellationToken);
    }

    public Task<bool> DeleteFrontByIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_historyBySystem.TryGetValue(systemId, out var history))
                return Task.FromResult(false);

            var entry = history.FirstOrDefault(x => string.Equals(x.FrontId, frontId, StringComparison.Ordinal));
            if (entry is null)
                return Task.FromResult(false);

            history.Remove(entry);

            if (_activeBySystem.TryGetValue(systemId, out var active) && active.ContainsKey(entry.AlterId))
            {
                active.TryRemove(entry.AlterId, out _);

                if (_primaryBySystem.TryGetValue(systemId, out var primary) && primary == entry.AlterId)
                    _primaryBySystem[systemId] = null;
            }

            return Task.FromResult(true);
        }
    }

    public Task<bool> UpdateCommentByFrontIdAsync(string systemId, string frontId, string comment, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_activeBySystem.TryGetValue(systemId, out var set))
                return Task.FromResult(false);

            var found = set.Values.FirstOrDefault(x => string.Equals(x.FrontId, frontId, StringComparison.Ordinal));
            if (found is null)
                return Task.FromResult(false);

            found.Comment = comment;

            if (_historyBySystem.TryGetValue(systemId, out var history))
            {
                var historical = history.LastOrDefault(
                    x => string.Equals(x.FrontId, frontId, StringComparison.Ordinal) && x.EndedAt is null);
                if (historical is not null)
                {
                    historical.Comment = comment;
                }
            }

            return Task.FromResult(true);
        }
    }

    private async Task<string?> ResolveFriendshipLevelAsync(string systemId, string? viewerSystemId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(viewerSystemId))
        {
            return null;
        }

        if (string.Equals(systemId, viewerSystemId, StringComparison.Ordinal))
        {
            return "trusted_friend";
        }

        if (_friendships is null)
        {
            return null;
        }

        return await _friendships.GetFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
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