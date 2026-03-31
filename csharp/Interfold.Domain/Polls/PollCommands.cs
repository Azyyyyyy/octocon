namespace Interfold.Domain.Polls;

public sealed record CreatePollCommand(
    string Title,
    string? Description,
    string Type,
    string? TimeEndIso
);

public sealed record UpdatePollCommand(
    string PollId,
    string? Title,
    string? Description,
    string? TimeEndIso,
    string? DataJson
);

public sealed record DeletePollCommand(string PollId);
