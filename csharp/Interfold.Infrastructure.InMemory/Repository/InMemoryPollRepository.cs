using System.Collections.Concurrent;
using System.Text.Json;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryPollRepository : IPollRepository
{
    private sealed class PollState
    {
        public required string PollId { get; init; }
        public required string UserId { get; init; }
        public required string Title { get; set; }
        public string? Description { get; set; }
        public required string Type { get; set; }
        public JsonElement Data { get; set; } = JsonElement.Parse("{}");
        public DateTime? TimeEnd { get; set; }
        public DateTime InsertedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private readonly IRegionContext _regionContext;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PollState>> _bySystem = new();

    public InMemoryPollRepository(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    public Task<IReadOnlyList<PollReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store))
            return Task.FromResult<IReadOnlyList<PollReadModel>>(Array.Empty<PollReadModel>());

        var list = store.Values
            .Select(ToReadModel)
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<PollReadModel>>(list);
    }

    public Task<PollReadModel?> GetAsync(string systemId, string pollId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(pollId, out var poll))
            return Task.FromResult<PollReadModel?>(null);

        return Task.FromResult<PollReadModel?>(ToReadModel(poll));
    }

    public Task<string?> CreateAsync(string systemId, CreatePollCommand command, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var store = _bySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<string, PollState>());
        var id = Guid.NewGuid().ToString("N");

        store[id] = new PollState
        {
            PollId = id,
            UserId = systemId,
            Title = command.Title,
            Description = command.Description,
            Type = command.Type,
            TimeEnd = command.TimeEnd,
            InsertedAt = command.InsertedAtUtc,
            UpdatedAt = DateTime.Now
        };

        return Task.FromResult<string?>(id);
    }

    public Task<bool> ExistsAsync(string systemId, string pollId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var exists = _bySystem.TryGetValue(systemKey, out var store) && store.ContainsKey(pollId);
        return Task.FromResult(exists);
    }

    public Task<bool> UpdateAsync(string systemId, UpdatePollCommand command, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(command.Id, out var poll))
            return Task.FromResult(false);

        if (command.Title is not null) poll.Title = command.Title;
        if (command.Description is not null) poll.Description = command.Description;
        if (command.HasTimeEnd) poll.TimeEnd = command.TimeEnd;
        if (command.Data is not null) poll.Data = command.Data.Value;
        poll.UpdatedAt = DateTime.Now;

        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string systemId, string pollId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store))
            return Task.FromResult(false);

        return Task.FromResult(store.TryRemove(pollId, out _));
    }

    public async Task RemoveAlterFromPollsAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store))
            return;

        var alterIdString = alterId.ToString();

        foreach (var poll in store.Values)
        {
            var data = poll.Data;
            if (data.ValueKind != JsonValueKind.Object) continue;

            var changed = false;
            var newEntries = new Dictionary<string, JsonElement>();

            foreach (var property in data.EnumerateObject())
            {
                if (property.Name == alterIdString)
                {
                    changed = true;
                    continue;
                }
                newEntries[property.Name] = property.Value;
            }

            if (changed)
            {
                poll.Data = JsonSerializer.SerializeToElement(newEntries);
                poll.UpdatedAt = DateTime.Now;
            }
        }
    }

    private static PollReadModel ToReadModel(PollState state)
        => new(
            state.PollId,
            state.UserId,
            state.Title,
            state.Description,
            state.Type,
            state.Data,
            state.TimeEnd,
            state.InsertedAt,
            state.UpdatedAt
        );

    private string GetSystemKey(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}
