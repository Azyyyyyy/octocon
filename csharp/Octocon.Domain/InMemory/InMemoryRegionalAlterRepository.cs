using System.Collections.Concurrent;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Alters;

namespace Octocon.Domain.InMemory;

public sealed class InMemoryRegionalAlterRepository : IAlterRepository
{
    private sealed class AlterState
    {
        public required int AlterId { get; init; }
        public string? Alias { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private readonly IRegionContext _regionContext;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, AlterState>> _bySystem = new();
    private readonly ConcurrentDictionary<string, int> _nextIdBySystem = new();

    public InMemoryRegionalAlterRepository(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    public Task<int?> CreateAsync(
        string systemId,
        CreateAlterCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var systemKey = GetSystemKey(systemId);
        var store = _bySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<int, AlterState>());
        var next = _nextIdBySystem.AddOrUpdate(systemKey, 1, (_, current) => current + 1);

        var created = store.TryAdd(next, new AlterState
        {
            AlterId = next,
            Name = command.Name
        });

        return Task.FromResult<int?>(created ? next : null);
    }

    public Task<bool> ExistsAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var exists = _bySystem.TryGetValue(systemKey, out var store) && store.ContainsKey(alterId);
        return Task.FromResult(exists);
    }

    public Task<bool> UpdateAsync(
        string systemId,
        UpdateAlterCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var systemKey = GetSystemKey(systemId);

        if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(command.AlterId, out var existing))
        {
            return Task.FromResult(false);
        }

        if (!string.IsNullOrWhiteSpace(command.Alias))
        {
            existing.Alias = command.Alias;
        }

        if (!string.IsNullOrWhiteSpace(command.Name))
        {
            existing.Name = command.Name;
        }

        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        if (!_bySystem.TryGetValue(systemKey, out var store))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(store.TryRemove(alterId, out _));
    }

    public Task<bool> AliasTakenByOtherAsync(
        string systemId,
        int alterId,
        string alias,
        CancellationToken cancellationToken = default
    )
    {
        var systemKey = GetSystemKey(systemId);

        if (!_bySystem.TryGetValue(systemKey, out var store))
        {
            return Task.FromResult(false);
        }

        var taken = store.Values.Any(a =>
            a.AlterId != alterId &&
            !string.IsNullOrWhiteSpace(a.Alias) &&
            string.Equals(a.Alias, alias, StringComparison.OrdinalIgnoreCase)
        );

        return Task.FromResult(taken);
    }

    private string GetSystemKey(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}