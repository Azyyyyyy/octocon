namespace Octocon.Domain.Abstractions;

public sealed record AccountCommandResult(string SystemId, string Username, bool Replay);

public sealed record AlterCommandResult(string SystemId, int AlterId, bool Replay);

public sealed record FrontCommandResult(string SystemId, int? AlterId, string? FrontId, bool Replay);

public sealed record TagCommandResult(string SystemId, string TagId, bool Replay);

public sealed record PollCommandResult(string SystemId, string PollId, bool Replay);

public sealed record GlobalJournalCommandResult(string SystemId, string EntryId, bool Replay);

public sealed record AlterJournalCommandResult(string SystemId, string EntryId, int AlterId, bool Replay);

public sealed record FriendshipCommandResult(string SystemId, string TargetSystemId, string Action, bool Replay);

public sealed record SettingsCommandResult(string SystemId, string Action, bool Replay);

public sealed record EncryptionCommandResult(string SystemId, string Action, string Key, bool Replay);