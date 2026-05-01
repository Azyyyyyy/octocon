using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Models.Read;

public sealed record PollReadModel(
    string Id,
    string UserId,
    string Title,
    string? Description,
    string Type,
    JsonElement Data,
    DateTime? TimeEnd,
    DateTime InsertedAt,
    DateTime UpdatedAt
);


public sealed record CreatePollRequest(
    string Title,
    string? Description = null,
    string? Type = null,
    DateTime? TimeEnd = null,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record UpdatePollRequest(
    string? Title = null,
    string? Description = null,
    JsonElement TimeEnd = default,
    JsonElement? Data = null,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey)
{
    [JsonIgnore]
    public bool HasTimeEnd => TimeEnd.ValueKind != JsonValueKind.Undefined;

    public bool TryResolveTimeEnd(out DateTime? timeEnd)
    {
        timeEnd = null;

        if (!HasTimeEnd || TimeEnd.ValueKind == JsonValueKind.Null)
            return true;

        if (TimeEnd.ValueKind == JsonValueKind.String && TimeEnd.TryGetDateTime(out var parsed))
        {
            timeEnd = parsed;
            return true;
        }

        return false;
    }
}

