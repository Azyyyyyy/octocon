using Interfold.Domain.Alters;

namespace Interfold.Domain.Fronting;

public sealed record FrontActiveReadModel(
    BareAlter Alter,
    FrontHistoryReadModel Front,
    bool Primary
);

public sealed record FrontHistoryReadModel(
    string Id,
    int AlterId,
    string? Comment,
    DateTimeOffset TimeStart,
    DateTimeOffset? TimeEnd,
    string UserId
);
