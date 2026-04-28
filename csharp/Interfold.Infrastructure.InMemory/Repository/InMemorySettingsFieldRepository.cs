using System.Collections.Concurrent;
using Interfold.Domain.Settings;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemorySettingsFieldRepository : ISettingsFieldRepository
{
    private readonly ConcurrentDictionary<string, List<SettingsFieldReadModel>> _bySystem = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<SettingsFieldReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store))
        {
            return Task.FromResult<IReadOnlyList<SettingsFieldReadModel>>(Array.Empty<SettingsFieldReadModel>());
        }

        lock (store)
        {
            var result = store
                .OrderBy(x => x.Index)
                .Select(x => x)
                .ToArray();
            return Task.FromResult<IReadOnlyList<SettingsFieldReadModel>>(result);
        }
    }

    public Task<string?> CreateAsync(
        string systemId,
        string name,
        string type,
        string securityLevel,
        bool locked,
        CancellationToken cancellationToken = default)
    {
        var store = _bySystem.GetOrAdd(systemId, _ => []);
        var fieldId = Guid.NewGuid().ToString("N");

        lock (store)
        {
            store.Add(new SettingsFieldReadModel(fieldId, name, type, securityLevel, locked, store.Count));
        }

        return Task.FromResult<string?>(fieldId);
    }

    public Task<bool> UpdateAsync(
        string systemId,
        string fieldId,
        string? name,
        string? securityLevel,
        bool? locked,
        CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store))
            return Task.FromResult(false);

        lock (store)
        {
            var index = store.FindIndex(x => string.Equals(x.Id, fieldId, StringComparison.Ordinal));
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            var existing = store[index];
            store[index] = existing with
            {
                Name = name ?? existing.Name,
                SecurityLevel = securityLevel ?? existing.SecurityLevel,
                Locked = locked ?? existing.Locked
            };
        }

        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string systemId, string fieldId, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store))
            return Task.FromResult(false);

        lock (store)
        {
            var index = store.FindIndex(x => string.Equals(x.Id, fieldId, StringComparison.Ordinal));
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            store.RemoveAt(index);
            Reindex(store);
            return Task.FromResult(true);
        }
    }

    public Task<bool> RelocateAsync(string systemId, string fieldId, int index, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store))
            return Task.FromResult(false);

        lock (store)
        {
            var oldIndex = store.FindIndex(x => string.Equals(x.Id, fieldId, StringComparison.Ordinal));
            if (oldIndex < 0)
            {
                return Task.FromResult(false);
            }

            var field = store[oldIndex];
            store.RemoveAt(oldIndex);

            var boundedIndex = Math.Max(0, Math.Min(index, store.Count));
            store.Insert(boundedIndex, field);
            Reindex(store);
            return Task.FromResult(true);
        }
    }

    private static void Reindex(List<SettingsFieldReadModel> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            fields[i] = fields[i] with { Index = i };
        }
    }
}
