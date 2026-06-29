using System.Text.Json;

namespace Interfold.Contracts.Models.Commands;

/// <summary>
/// Command payload for creating a poll. <see cref="InsertedAtUtc"/> is a server-side
/// timestamp supplied by the caller: the public API uses "now" (matching the envelope's
/// <c>OccurredAt</c>), while the Simply Plural import passes SP's <c>lastOperationTime</c>
/// so historical polls keep their original creation date instead of all landing under
/// "today" in time-ordered views.
/// </summary>
public sealed record CreatePollCommand(
    string Title,
    string? Description,
    string Type,
    DateTime? TimeEnd,
    DateTime InsertedAtUtc
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
