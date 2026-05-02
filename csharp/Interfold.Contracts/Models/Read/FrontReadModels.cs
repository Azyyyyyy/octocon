using System.Text.Json.Serialization;

namespace Interfold.Contracts.Models.Read;

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


public sealed record FrontBulkUpdateRequest(
    IReadOnlyList<FrontStartEntry> Start,
    IReadOnlyList<int> End,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record FrontStartEntry(int AlterId, string? Comment = null);

public sealed record FrontStartRequest(
    int? AlterId,
    [property: JsonPropertyName("id")] int? Id = null,
    string? Comment = null,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey)
{
    public int ResolveAlterId() => AlterId ?? Id ?? 0;
}

public sealed record FrontEndRequest(
    int? AlterId,
    [property: JsonPropertyName("id")] int? Id = null,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey)
{
    public int ResolveAlterId() => AlterId ?? Id ?? 0;
}

public sealed record FrontPrimaryRequest(
    int? AlterId = null,
    [property: JsonPropertyName("id")] int? Id = null,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey)
{
    public int? ResolveAlterId() => AlterId ?? Id;
}

public sealed record FrontSetRequest(
    int? AlterId,
    [property: JsonPropertyName("id")] int? Id = null,
    string? Comment = null,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey)
{
    public int ResolveAlterId() => AlterId ?? Id ?? 0;
}

public sealed record FrontCommentRequest(
    string Comment,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record FrontStartedResponse(string FrontId);

