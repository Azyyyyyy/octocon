using System.Collections.Concurrent;
using Octocon.Domain.Journals;

namespace Octocon.Domain.InMemory;

public sealed class InMemoryJournalRepository : IJournalRepository
{
    private sealed class EntryState
    {
        public required string EntryId { get; init; }
        public required string Title { get; set; }
        public string? Content { get; set; }
        public string? Color { get; set; }
    }

    private sealed class AlterEntryState
    {
        public required string EntryId { get; init; }
        public required int AlterId { get; init; }
        public required string Title { get; set; }
        public string? Content { get; set; }
        public string? Color { get; set; }
        public bool Pinned { get; set; }
        public bool Locked { get; set; }
    }

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, EntryState>> _bySystem = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (bool Pinned, bool Locked)>> _stateBySystem = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, bool>> _entryAlters = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, AlterEntryState>> _alterEntriesBySystem = new();

    public Task<string?> CreateGlobalAsync(string systemId, CreateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        var store = _bySystem.GetOrAdd(systemId, _ => new ConcurrentDictionary<string, EntryState>());
        var id = Guid.NewGuid().ToString("N");

        store[id] = new EntryState
        {
            EntryId = id,
            Title = command.Title,
            Content = null,
            Color = null
        };

        return Task.FromResult<string?>(id);
    }

    public Task<bool> ExistsGlobalAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        var exists = _bySystem.TryGetValue(systemId, out var store) && store.ContainsKey(entryId);
        return Task.FromResult(exists);
    }

    public Task<bool> UpdateGlobalAsync(string systemId, UpdateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store) || !store.TryGetValue(command.EntryId, out var entry))
            return Task.FromResult(false);

        if (command.Title is not null) entry.Title = command.Title;
        if (command.Content is not null) entry.Content = command.Content;
        if (command.Color is not null) entry.Color = command.Color;

        return Task.FromResult(true);
    }

    public Task<bool> DeleteGlobalAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store))
            return Task.FromResult(false);

        var removed = store.TryRemove(entryId, out _);
        if (removed)
        {
            if (_stateBySystem.TryGetValue(systemId, out var stateStore))
                stateStore.TryRemove(entryId, out _);

            _entryAlters.TryRemove(GetEntryKey(systemId, entryId), out _);
        }

        return Task.FromResult(removed);
    }

    public Task<bool> SetGlobalLockedAsync(string systemId, string entryId, bool locked, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store) || !store.ContainsKey(entryId))
            return Task.FromResult(false);

        var stateStore = _stateBySystem.GetOrAdd(systemId, _ => new ConcurrentDictionary<string, (bool Pinned, bool Locked)>());
        var current = stateStore.GetOrAdd(entryId, _ => (false, false));
        stateStore[entryId] = (current.Pinned, locked);
        return Task.FromResult(true);
    }

    public Task<bool> SetGlobalPinnedAsync(string systemId, string entryId, bool pinned, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store) || !store.ContainsKey(entryId))
            return Task.FromResult(false);

        var stateStore = _stateBySystem.GetOrAdd(systemId, _ => new ConcurrentDictionary<string, (bool Pinned, bool Locked)>());
        var current = stateStore.GetOrAdd(entryId, _ => (false, false));
        stateStore[entryId] = (pinned, current.Locked);
        return Task.FromResult(true);
    }

    public Task<bool> AttachGlobalAlterAsync(string systemId, string entryId, int alterId, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store) || !store.ContainsKey(entryId))
            return Task.FromResult(false);

        var key = GetEntryKey(systemId, entryId);
        var alters = _entryAlters.GetOrAdd(key, _ => new ConcurrentDictionary<int, bool>());
        alters[alterId] = true;
        return Task.FromResult(true);
    }

    public Task<bool> DetachGlobalAlterAsync(string systemId, string entryId, int alterId, CancellationToken cancellationToken = default)
    {
        var key = GetEntryKey(systemId, entryId);
        if (!_entryAlters.TryGetValue(key, out var alters))
            return Task.FromResult(false);

        return Task.FromResult(alters.TryRemove(alterId, out _));
    }

    public Task<string?> CreateAlterAsync(string systemId, CreateAlterJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        var store = _alterEntriesBySystem.GetOrAdd(systemId, _ => new ConcurrentDictionary<string, AlterEntryState>());
        var entryId = Guid.NewGuid().ToString("N");

        store[entryId] = new AlterEntryState
        {
            EntryId = entryId,
            AlterId = command.AlterId,
            Title = command.Title,
            Content = null,
            Color = null,
            Pinned = false,
            Locked = false
        };

        return Task.FromResult<string?>(entryId);
    }

    public Task<AlterJournalRef?> GetAlterRefAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        if (!_alterEntriesBySystem.TryGetValue(systemId, out var store) || !store.TryGetValue(entryId, out var entry))
            return Task.FromResult<AlterJournalRef?>(null);

        return Task.FromResult<AlterJournalRef?>(new AlterJournalRef(entry.EntryId, entry.AlterId));
    }

    public Task<bool> UpdateAlterAsync(string systemId, UpdateAlterJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        if (!_alterEntriesBySystem.TryGetValue(systemId, out var store) || !store.TryGetValue(command.EntryId, out var entry))
            return Task.FromResult(false);

        if (command.Title is not null) entry.Title = command.Title;
        if (command.Content is not null) entry.Content = command.Content;
        if (command.Color is not null) entry.Color = command.Color;

        return Task.FromResult(true);
    }

    public Task<bool> DeleteAlterAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        if (!_alterEntriesBySystem.TryGetValue(systemId, out var store))
            return Task.FromResult(false);

        return Task.FromResult(store.TryRemove(entryId, out _));
    }

    public Task<bool> SetAlterLockedAsync(string systemId, string entryId, bool locked, CancellationToken cancellationToken = default)
    {
        if (!_alterEntriesBySystem.TryGetValue(systemId, out var store) || !store.TryGetValue(entryId, out var entry))
            return Task.FromResult(false);

        entry.Locked = locked;
        return Task.FromResult(true);
    }

    public Task<bool> SetAlterPinnedAsync(string systemId, string entryId, bool pinned, CancellationToken cancellationToken = default)
    {
        if (!_alterEntriesBySystem.TryGetValue(systemId, out var store) || !store.TryGetValue(entryId, out var entry))
            return Task.FromResult(false);

        entry.Pinned = pinned;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<AlterJournalReadModel>> ListAlterAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        if (!_alterEntriesBySystem.TryGetValue(systemId, out var store))
            return Task.FromResult<IReadOnlyList<AlterJournalReadModel>>(Array.Empty<AlterJournalReadModel>());

        var entries = store.Values
            .Where(e => e.AlterId == alterId)
            .OrderByDescending(e => e.EntryId)
            .Select(e => new AlterJournalReadModel(e.EntryId, e.AlterId, e.Title, e.Content, e.Color, e.Pinned, e.Locked))
            .ToArray();

        return Task.FromResult<IReadOnlyList<AlterJournalReadModel>>(entries);
    }

    public Task<AlterJournalReadModel?> GetAlterAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        if (!_alterEntriesBySystem.TryGetValue(systemId, out var store) || !store.TryGetValue(entryId, out var entry))
            return Task.FromResult<AlterJournalReadModel?>(null);

        return Task.FromResult<AlterJournalReadModel?>(
            new AlterJournalReadModel(entry.EntryId, entry.AlterId, entry.Title, entry.Content, entry.Color, entry.Pinned, entry.Locked)
        );
    }

    public Task<IReadOnlyList<GlobalJournalReadModel>> ListGlobalAsync(string systemId, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store))
            return Task.FromResult<IReadOnlyList<GlobalJournalReadModel>>(Array.Empty<GlobalJournalReadModel>());

        var entries = store.Values
            .OrderByDescending(e => e.EntryId)
            .Select(e =>
            {
                var (pinned, locked) = GetGlobalState(systemId, e.EntryId);
                var alterIds = GetGlobalAlterIds(systemId, e.EntryId);
                return new GlobalJournalReadModel(e.EntryId, e.Title, e.Content, e.Color, pinned, locked, alterIds);
            })
            .ToArray();

        return Task.FromResult<IReadOnlyList<GlobalJournalReadModel>>(entries);
    }

    public Task<GlobalJournalReadModel?> GetGlobalAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store) || !store.TryGetValue(entryId, out var entry))
            return Task.FromResult<GlobalJournalReadModel?>(null);

        var (pinned, locked) = GetGlobalState(systemId, entryId);
        var alterIds = GetGlobalAlterIds(systemId, entryId);
        return Task.FromResult<GlobalJournalReadModel?>(
            new GlobalJournalReadModel(entry.EntryId, entry.Title, entry.Content, entry.Color, pinned, locked, alterIds));
    }

    private (bool Pinned, bool Locked) GetGlobalState(string systemId, string entryId)
    {
        if (_stateBySystem.TryGetValue(systemId, out var stateStore) &&
            stateStore.TryGetValue(entryId, out var state))
            return state;
        return (false, false);
    }

    private IReadOnlyList<int> GetGlobalAlterIds(string systemId, string entryId)
    {
        var key = GetEntryKey(systemId, entryId);
        if (!_entryAlters.TryGetValue(key, out var alters))
            return Array.Empty<int>();
        return alters.Keys.ToArray();
    }

    private static string GetEntryKey(string systemId, string entryId) => $"{systemId}:{entryId}";
}
