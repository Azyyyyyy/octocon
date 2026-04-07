using System.Text.Json;

namespace Interfold.Domain.Polls;

public sealed record CreatePollCommand(
    string Title,
    string? Description,
    string Type,
    DateTime? TimeEnd
);

public sealed record UpdatePollCommand(
    string Id,
    string? Title,
    string? Description,
    DateTime? TimeEnd,
    bool HasTimeEnd,
    JsonElement? Data
);

public sealed record DeletePollCommand(string PollId);
