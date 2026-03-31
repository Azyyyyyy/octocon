namespace Interfold.Domain.Polls;

public sealed record PollReadModel(
    string PollId,
    string Title,
    string? Description,
    string Type,
    string DataJson,
    DateTimeOffset? TimeEnd
);
