using System.Collections.Concurrent;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryJournalRepository : IJournalRepository
{
    private sealed class EntryState
    {
        public required string EntryId { get; init; }
        public required string UserId { get; init; }
        public required string Title { get; set; }
        public string? Content { get; set; }
        public string? Color { get; set; }
        public required DateTime InsertedAt { get; init; }
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class AlterEntryState
    {
        public required string EntryId { get; init; }
        public required string UserId { get; init; }
        public required int AlterId { get; init; }
        public required string Title { get; set; }
        public string? Content { get; set; }
        public string? Color { get; set; }
        public bool Pinned { get; set; }
        public bool Locked { get; set; }
        public required DateTime InsertedAt { get; init; }
        public DateTime UpdatedAt { get; set; }
    }

    private readonly IRegionContext _regionContext;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, EntryState>> _bySystem = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (bool Pinned, bool Locked)>> _stateBySystem = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, bool>> _entryAlters = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, AlterEntryState>> _alterEntriesBySystem = new();

    public InMemoryJournalRepository(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    public Task<string?> CreateGlobalAsync(string systemId, CreateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var store = _bySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<string, EntryState>());
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.Now;

        store[id] = new EntryState
        {
            EntryId = id,
            UserId = systemId,
            Title = command.Title,
            Content = null,
            Color = null,
            InsertedAt = now,
            UpdatedAt = now
        };

        return Task.FromResult<string?>(id);
    }

    public Task<bool> ExistsGlobalAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var exists = _bySystem.TryGetValue(systemKey, out var store) && store.ContainsKey(entryId);
        return Task.FromResult(exists);
    }

    public Task<bool> UpdateGlobalAsync(string systemId, UpdateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(command.EntryId, out var entry))
            return Task.FromResult(false);

        if (command.Title is not null) entry.Title = command.Title;
        if (command.Content is not null) entry.Content = command.Content;
        if (command.Color is not null) entry.Color = command.Color;
        entry.UpdatedAt = DateTime.Now;

        return Task.FromResult(true);
    }

    public Task<bool> DeleteGlobalAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store))
            return Task.FromResult(false);

        var removed = store.TryRemove(entryId, out _);
        if (removed)
        {
            if (_stateBySystem.TryGetValue(systemKey, out var stateStore))
                stateStore.TryRemove(entryId, out _);

            _entryAlters.TryRemove(GetEntryKey(systemId, entryId), out _);
        }

        return Task.FromResult(removed);
    }

    public Task<bool> SetGlobalLockedAsync(string systemId, string entryId, bool locked, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store) || !store.ContainsKey(entryId))
            return Task.FromResult(false);

        var stateStore = _stateBySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<string, (bool Pinned, bool Locked)>());
        var current = stateStore.GetOrAdd(entryId, _ => (false, false));
        stateStore[entryId] = (current.Pinned, locked);
        return Task.FromResult(true);
    }

    public Task<bool> SetGlobalPinnedAsync(string systemId, string entryId, bool pinned, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store) || !store.ContainsKey(entryId))
            return Task.FromResult(false);

        var stateStore = _stateBySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<string, (bool Pinned, bool Locked)>());
        var current = stateStore.GetOrAdd(entryId, _ => (false, false));
        stateStore[entryId] = (pinned, current.Locked);
        return Task.FromResult(true);
    }

    public Task<bool> AttachGlobalAlterAsync(string systemId, string entryId, int alterId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store) || !store.ContainsKey(entryId))
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
        var systemKey = GetSystemKey(systemId);
        var store = _alterEntriesBySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<string, AlterEntryState>());
        var entryId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        store[entryId] = new AlterEntryState
        {
            EntryId = entryId,
            UserId = systemId,
            AlterId = command.AlterId,
            Title = command.Title,
            Content = null,
            Color = null,
            Pinned = false,
            Locked = false,
            InsertedAt = now,
            UpdatedAt = now
        };

        return Task.FromResult<string?>(entryId);
    }

    public Task<AlterJournalRef?> GetAlterRefAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_alterEntriesBySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(entryId, out var entry))
            return Task.FromResult<AlterJournalRef?>(null);

        return Task.FromResult<AlterJournalRef?>(new AlterJournalRef(entry.EntryId, entry.AlterId));
    }

    public Task<bool> UpdateAlterAsync(string systemId, UpdateAlterJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_alterEntriesBySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(command.EntryId, out var entry))
            return Task.FromResult(false);

        if (command.Title is not null) entry.Title = command.Title;
        if (command.Content is not null) entry.Content = command.Content;
        if (command.Color is not null) entry.Color = command.Color;
        entry.UpdatedAt = DateTime.UtcNow;

        return Task.FromResult(true);
    }

    public Task<bool> DeleteAlterAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_alterEntriesBySystem.TryGetValue(systemKey, out var store))
            return Task.FromResult(false);

        return Task.FromResult(store.TryRemove(entryId, out _));
    }

    public Task<bool> SetAlterLockedAsync(string systemId, string entryId, bool locked, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_alterEntriesBySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(entryId, out var entry))
            return Task.FromResult(false);

        entry.Locked = locked;
        entry.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(true);
    }

    public Task<bool> SetAlterPinnedAsync(string systemId, string entryId, bool pinned, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_alterEntriesBySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(entryId, out var entry))
            return Task.FromResult(false);

        entry.Pinned = pinned;
        entry.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<AlterJournalReadModel>> ListAlterAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_alterEntriesBySystem.TryGetValue(systemKey, out var store))
            return Task.FromResult<IReadOnlyList<AlterJournalReadModel>>(Array.Empty<AlterJournalReadModel>());

        var entries = store.Values
            .Where(e => e.AlterId == alterId)
            .OrderByDescending(e => e.InsertedAt)
            .Select(e => new AlterJournalReadModel(e.EntryId, e.UserId, e.AlterId, e.Title, e.Content, e.Color, e.Locked, e.Pinned, e.InsertedAt, e.UpdatedAt))
            .ToArray();

        return Task.FromResult<IReadOnlyList<AlterJournalReadModel>>(entries);
    }

    public Task<AlterJournalReadModel?> GetAlterAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_alterEntriesBySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(entryId, out var entry))
            return Task.FromResult<AlterJournalReadModel?>(null);

        return Task.FromResult<AlterJournalReadModel?>(
            new AlterJournalReadModel(entry.EntryId, entry.UserId, entry.AlterId, entry.Title, entry.Content, entry.Color, entry.Locked, entry.Pinned, entry.InsertedAt, entry.UpdatedAt)
        );
    }

    public Task<IReadOnlyList<JournalReadModel>> ListGlobalAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store))
            return Task.FromResult<IReadOnlyList<JournalReadModel>>(Array.Empty<JournalReadModel>());

        var entries = store.Values
            .OrderByDescending(e => e.EntryId)
            .Select(e =>
            {
                var (pinned, locked) = GetGlobalState(systemId, e.EntryId);
                var alterIds = GetGlobalAlterIds(systemId, e.EntryId);
                return new JournalReadModel(
                    e.EntryId,
                    e.UserId,
                    e.Title,
                    e.Content,
                    e.Color,
                    locked,
                    pinned,
                    e.InsertedAt,
                    e.UpdatedAt,
                    alterIds);
            })
            .ToArray();

        return Task.FromResult<IReadOnlyList<JournalReadModel>>(entries);
    }

    public Task<JournalReadModel?> GetGlobalAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(entryId, out var entry))
            return Task.FromResult<JournalReadModel?>(null);

        var (pinned, locked) = GetGlobalState(systemId, entryId);
        var alterIds = GetGlobalAlterIds(systemId, entryId);
        return Task.FromResult<JournalReadModel?>(
            new JournalReadModel(
                entry.EntryId,
                entry.UserId,
                entry.Title,
                entry.Content,
                entry.Color,
                locked,
                pinned,
                entry.InsertedAt,
                entry.UpdatedAt,
                alterIds));
    }

    private (bool Pinned, bool Locked) GetGlobalState(string systemId, string entryId)
    {
        var systemKey = GetSystemKey(systemId);
        if (_stateBySystem.TryGetValue(systemKey, out var stateStore) &&
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

    private string GetEntryKey(string systemId, string entryId) => $"{GetSystemKey(systemId)}:{entryId}";

    private string GetSystemKey(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}
