using System.Text.Json;

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
