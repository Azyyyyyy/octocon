using System.Collections.Concurrent;
using Octocon.Domain.Settings;

namespace Octocon.Domain.InMemory;

public sealed class InMemorySettingsFieldRepository : ISettingsFieldRepository
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SettingsFieldReadModel>> _bySystem = new(StringComparer.Ordinal);

    public Task<string?> CreateAsync(string systemId, string name, string? value, int position, CancellationToken cancellationToken = default)
    {
        var store = _bySystem.GetOrAdd(systemId, _ => new(StringComparer.Ordinal));
        var fieldId = Guid.NewGuid().ToString("N");
        store[fieldId] = new SettingsFieldReadModel(fieldId, name, value, position);
        return Task.FromResult<string?>(fieldId);
    }

    public Task<bool> UpdateAsync(string systemId, string fieldId, string? name, string? value, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store) || !store.TryGetValue(fieldId, out var existing))
            return Task.FromResult(false);

        store[fieldId] = existing with
        {
            Name = name ?? existing.Name,
            Value = value ?? existing.Value
        };

        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string systemId, string fieldId, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store))
            return Task.FromResult(false);

        return Task.FromResult(store.TryRemove(fieldId, out _));
    }

    public Task<bool> RelocateAsync(string systemId, string fieldId, int position, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store) || !store.TryGetValue(fieldId, out var existing))
            return Task.FromResult(false);

        store[fieldId] = existing with { Position = position };
        return Task.FromResult(true);
    }
}
