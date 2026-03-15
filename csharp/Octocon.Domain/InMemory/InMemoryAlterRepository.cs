using System.Collections.Concurrent;
using Octocon.Domain.Alters;

namespace Octocon.Domain.InMemory;

public sealed class InMemoryAlterRepository : IAlterRepository
{
    private sealed class AlterState
    {
        public required int AlterId { get; init; }
        public string? Alias { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, AlterState>> _bySystem = new();
    private readonly ConcurrentDictionary<string, int> _nextIdBySystem = new();

    public Task<int?> CreateAsync(
        string systemId,
        CreateAlterCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var store = _bySystem.GetOrAdd(systemId, _ => new ConcurrentDictionary<int, AlterState>());
        var next = _nextIdBySystem.AddOrUpdate(systemId, 1, (_, current) => current + 1);

        var created = store.TryAdd(next, new AlterState
        {
            AlterId = next,
            Name = command.Name
        });

        return Task.FromResult<int?>(created ? next : null);
    }

    public Task<bool> ExistsAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        var exists = _bySystem.TryGetValue(systemId, out var store) && store.ContainsKey(alterId);
        return Task.FromResult(exists);
    }

    public Task<bool> UpdateAsync(
        string systemId,
        UpdateAlterCommand command,
        CancellationToken cancellationToken = default
    )
    {
        if (!_bySystem.TryGetValue(systemId, out var store) || !store.TryGetValue(command.AlterId, out var existing))
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
        if (!_bySystem.TryGetValue(systemId, out var store))
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
        if (!_bySystem.TryGetValue(systemId, out var store))
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
}