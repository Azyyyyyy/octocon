using System.Collections.Concurrent;
using System.Text.Json;
using Interfold.Domain.Polls;

namespace Interfold.Domain.InMemory;

public sealed class InMemoryPollRepository : IPollRepository
{
    private sealed class PollState
    {
        public required string PollId { get; init; }
        public required string UserId { get; init; }
        public required string Title { get; set; }
        public string? Description { get; set; }
        public required string Type { get; set; }
        public string Data { get; set; } = "{}";
        public string? TimeEndIso { get; set; }
        public DateTime InsertedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PollState>> _bySystem = new();

    public Task<IReadOnlyList<PollReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store))
            return Task.FromResult<IReadOnlyList<PollReadModel>>(Array.Empty<PollReadModel>());

        var list = store.Values
            .Select(ToReadModel)
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<PollReadModel>>(list);
    }

    public Task<PollReadModel?> GetAsync(string systemId, string pollId, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store) || !store.TryGetValue(pollId, out var poll))
            return Task.FromResult<PollReadModel?>(null);

        return Task.FromResult<PollReadModel?>(ToReadModel(poll));
    }

    public Task<string?> CreateAsync(string systemId, CreatePollCommand command, CancellationToken cancellationToken = default)
    {
        var store = _bySystem.GetOrAdd(systemId, _ => new ConcurrentDictionary<string, PollState>());
        var id = Guid.NewGuid().ToString("N");

        store[id] = new PollState
        {
            PollId = id,
            UserId = systemId,
            Title = command.Title,
            Description = command.Description,
            Type = command.Type,
            TimeEndIso = command.TimeEndIso,
            InsertedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        return Task.FromResult<string?>(id);
    }

    public Task<bool> ExistsAsync(string systemId, string pollId, CancellationToken cancellationToken = default)
    {
        var exists = _bySystem.TryGetValue(systemId, out var store) && store.ContainsKey(pollId);
        return Task.FromResult(exists);
    }

    public Task<bool> UpdateAsync(string systemId, UpdatePollCommand command, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store) || !store.TryGetValue(command.PollId, out var poll))
            return Task.FromResult(false);

        if (command.Title is not null) poll.Title = command.Title;
        if (command.Description is not null) poll.Description = command.Description;
        if (command.TimeEndIso is not null) poll.TimeEndIso = command.TimeEndIso;
        if (command.DataJson is not null) poll.Data = command.DataJson;
        poll.UpdatedAt = DateTime.Now;

        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string systemId, string pollId, CancellationToken cancellationToken = default)
    {
        if (!_bySystem.TryGetValue(systemId, out var store))
            return Task.FromResult(false);

        return Task.FromResult(store.TryRemove(pollId, out _));
    }

    private static PollReadModel ToReadModel(PollState state)
        => new(
            state.PollId,
            state.UserId,
            state.Title,
            state.Description,
            state.Type,
            JsonSerializer.Deserialize<JsonElement>(state.Data ?? "{}"),
            DateTime.TryParse(state.TimeEndIso, out var ts) ? ts : null,
            state.InsertedAt,
            state.UpdatedAt
        );
}
