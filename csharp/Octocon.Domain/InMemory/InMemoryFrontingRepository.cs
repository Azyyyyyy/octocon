using System.Collections.Concurrent;
using Octocon.Domain.Fronting;

namespace Octocon.Domain.InMemory;

public sealed class InMemoryFrontingRepository : IFrontingRepository
{
    private sealed class FrontState
    {
        public required string FrontId { get; init; }
        public required int AlterId { get; init; }
        public string? Comment { get; set; }
        public required DateTimeOffset StartedAt { get; init; }
    }

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, FrontState>> _activeBySystem = new();
    private readonly ConcurrentDictionary<string, int?> _primaryBySystem = new();
    private readonly object _sync = new();

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

            var removed = set.TryRemove(alterId, out _);
            if (removed && _primaryBySystem.TryGetValue(systemId, out var currentPrimary) && currentPrimary == alterId)
            {
                _primaryBySystem[systemId] = null;
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
        lock (_sync)
        {
            if (!_activeBySystem.TryGetValue(systemId, out var set))
                return Task.FromResult(false);

            var found = set.Values.FirstOrDefault(x => string.Equals(x.FrontId, frontId, StringComparison.Ordinal));
            if (found is null)
                return Task.FromResult(false);

            found.Comment = comment;
            return Task.FromResult(true);
        }
    }
}