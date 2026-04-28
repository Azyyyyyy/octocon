namespace Interfold.Contracts.Models.Commands;

public sealed record StartFrontCommand(int AlterId, string? Comment);

public sealed record EndFrontCommand(int AlterId);

public sealed record SetPrimaryFrontCommand(int? AlterId);

public sealed record FrontStartItem(int AlterId, string? Comment);

public sealed record BulkUpdateFrontCommand(IReadOnlyList<FrontStartItem> Start, IReadOnlyList<int> End);

public sealed record SetFrontCommand(int AlterId, string? Comment);

public sealed record DeleteFrontByIdCommand(string FrontId);

public sealed record UpdateFrontCommentCommand(string FrontId, string Comment);
