namespace Interfold.Contracts.Models.Commands;

/// <summary>
/// Public-API callers send <c>InsertedAtUtc: default(DateTime)</c>. The handler stamps the
/// real value from the envelope's <c>OccurredAt</c> after the idempotency hash is taken, so
/// the hashed payload stays stable across retries (otherwise every replay would carry a fresh
/// <c>DateTime.UtcNow</c> and trip <c>ConflictDuplicate</c>). The Simply Plural importer
/// constructs the command directly with the decoded ObjectId timestamp.
/// </summary>
public sealed record CreateTagCommand(string Name, string? ParentTagId, DateTime InsertedAtUtc);

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
