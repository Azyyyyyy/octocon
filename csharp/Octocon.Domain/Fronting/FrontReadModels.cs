namespace Octocon.Domain.Fronting;

public sealed record FrontActiveReadModel(
    string FrontId,
    int AlterId,
    string? Comment,
    DateTimeOffset StartedAt,
    bool IsPrimary
);

public sealed record FrontHistoryReadModel(
    string FrontId,
    int AlterId,
    string? Comment,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt
);
