using System.Collections.Concurrent;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Fronting;

namespace Octocon.Domain.InMemory;

public sealed class InMemoryRegionalFrontingRepository : IFrontingRepository
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

    private readonly IRegionContext _regionContext;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, FrontState>> _activeBySystem = new();
    private readonly ConcurrentDictionary<string, List<FrontHistoryState>> _historyBySystem = new();
    private readonly ConcurrentDictionary<string, int?> _primaryBySystem = new();
    private readonly object _sync = new();

    public InMemoryRegionalFrontingRepository(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    public Task<bool> IsFrontingAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            var active = _activeBySystem.TryGetValue(systemKey, out var set) && set.ContainsKey(alterId);
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
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            var set = _activeBySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<int, FrontState>());
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

            var history = _historyBySystem.GetOrAdd(systemKey, _ => new List<FrontHistoryState>());
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
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            if (!_activeBySystem.TryGetValue(systemKey, out var set))
            {
                return Task.FromResult(false);
            }

            var removed = set.TryRemove(alterId, out var removedFront);
            if (removed && _primaryBySystem.TryGetValue(systemKey, out var currentPrimary) && currentPrimary == alterId)
            {
                _primaryBySystem[systemKey] = null;
            }

            if (removed && removedFront is not null && _historyBySystem.TryGetValue(systemKey, out var history))
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
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            if (alterId is int value)
            {
                if (!_activeBySystem.TryGetValue(systemKey, out var set) || !set.ContainsKey(value))
                {
                    return Task.FromResult(false);
                }
            }

            _primaryBySystem[systemKey] = alterId;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<FrontActiveReadModel>> ListActiveAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            if (!_activeBySystem.TryGetValue(systemKey, out var set))
                return Task.FromResult<IReadOnlyList<FrontActiveReadModel>>(Array.Empty<FrontActiveReadModel>());

            _primaryBySystem.TryGetValue(systemKey, out var primary);

            var results = set.Values
                .Select(x => new FrontActiveReadModel(
                    x.FrontId,
                    x.AlterId,
                    x.Comment,
                    x.StartedAt,
                    primary == x.AlterId))
                .OrderByDescending(x => x.StartedAt)
                .ToArray();

            return Task.FromResult<IReadOnlyList<FrontActiveReadModel>>(results);
        }
    }

    public Task<IReadOnlyList<FrontHistoryReadModel>> ListHistoryBetweenAsync(
        string systemId,
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            if (!_historyBySystem.TryGetValue(systemKey, out var history))
            {
                return Task.FromResult<IReadOnlyList<FrontHistoryReadModel>>(Array.Empty<FrontHistoryReadModel>());
            }

            var results = history
                .Where(x => x.StartedAt >= startInclusive && x.StartedAt <= endInclusive)
                .OrderByDescending(x => x.StartedAt)
                .Select(x => new FrontHistoryReadModel(x.FrontId, x.AlterId, x.Comment, x.StartedAt, x.EndedAt))
                .ToArray();

            return Task.FromResult<IReadOnlyList<FrontHistoryReadModel>>(results);
        }
    }

    public Task<FrontActiveReadModel?> GetActiveByFrontIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            if (!_activeBySystem.TryGetValue(systemKey, out var set))
                return Task.FromResult<FrontActiveReadModel?>(null);

            _primaryBySystem.TryGetValue(systemKey, out var primary);

            var found = set.Values.FirstOrDefault(x => string.Equals(x.FrontId, frontId, StringComparison.Ordinal));
            if (found is null)
                return Task.FromResult<FrontActiveReadModel?>(null);

            return Task.FromResult<FrontActiveReadModel?>(new FrontActiveReadModel(
                found.FrontId,
                found.AlterId,
                found.Comment,
                found.StartedAt,
                primary == found.AlterId));
        }
    }

    public async Task<bool> EndByFrontIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default)
    {
        var found = await GetActiveByFrontIdAsync(systemId, frontId, cancellationToken);
        if (found is null)
            return false;

        return await EndAsync(systemId, found.AlterId, cancellationToken);
    }

    public Task<bool> UpdateCommentByFrontIdAsync(string systemId, string frontId, string comment, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            if (!_activeBySystem.TryGetValue(systemKey, out var set))
                return Task.FromResult(false);

            var found = set.Values.FirstOrDefault(x => string.Equals(x.FrontId, frontId, StringComparison.Ordinal));
            if (found is null)
                return Task.FromResult(false);

            found.Comment = comment;

            if (_historyBySystem.TryGetValue(systemKey, out var history))
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

    private string GetSystemKey(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}