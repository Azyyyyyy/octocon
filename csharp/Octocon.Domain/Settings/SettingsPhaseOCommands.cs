namespace Octocon.Domain.Settings;

public sealed record UploadAvatarCommand(string AvatarUrl);

public sealed record DeleteAvatarCommand();

public sealed record ImportPkCommand(string Token);

public sealed record ImportSpCommand(string Token);

public sealed record UnlinkDiscordCommand();

public sealed record UnlinkEmailCommand();

public sealed record UnlinkAppleCommand();

public sealed record DeleteAccountCommand();

public sealed record WipeAltersCommand();

public sealed record CreateFieldCommand(string Name, string Type, string SecurityLevel, bool Locked);

public sealed record UpdateFieldCommand(string FieldId, string? Name, string? SecurityLevel, bool? Locked);

public sealed record DeleteFieldCommand(string FieldId);

public sealed record RelocateFieldCommand(string FieldId, int Index);
