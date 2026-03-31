namespace Interfold.Domain.Tags;

public sealed record CreateTagCommand(string Name, string? ParentTagId);

public sealed record UpdateTagCommand(
	string TagId,
	string? Name,
	string? Color,
	string? Description,
	string? SecurityLevel
);

public sealed record DeleteTagCommand(string TagId);

public sealed record AttachAlterToTagCommand(string TagId, int AlterId);

public sealed record DetachAlterFromTagCommand(string TagId, int AlterId);

public sealed record SetParentTagCommand(string TagId, string ParentTagId);

public sealed record RemoveParentTagCommand(string TagId);
