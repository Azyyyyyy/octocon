namespace Interfold.Contracts.Models.Commands;

public sealed record CreateFieldCommand(string Name, string Type, string SecurityLevel, bool Locked);

public sealed record UpdateFieldCommand(string FieldId, string? Name, string? SecurityLevel, bool? Locked);

public sealed record DeleteFieldCommand(string FieldId);

public sealed record RelocateFieldCommand(string FieldId, int Index);
