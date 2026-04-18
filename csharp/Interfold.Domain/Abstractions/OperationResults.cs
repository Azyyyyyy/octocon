namespace Interfold.Domain.Abstractions;

/// <summary>
/// Marker interface for all command result records that carry a replay flag.
/// Used by the API layer to record metrics and structured log outcomes uniformly.
/// </summary>
public interface ICommandResult
{
    bool Replay { get; }
}

public sealed record AccountCommandResult(string SystemId, string Username, bool Replay) : ICommandResult;

public sealed record AlterCommandResult(string SystemId, int AlterId, bool Replay) : ICommandResult;

public sealed record FrontCommandResult(string SystemId, int? AlterId, string? FrontId, bool Replay) : ICommandResult;

public sealed record TagCommandResult(string SystemId, string TagId, bool Replay) : ICommandResult;

public sealed record PollCommandResult(string SystemId, string PollId, bool Replay) : ICommandResult;

public sealed record GlobalJournalCommandResult(string SystemId, string EntryId, bool Replay) : ICommandResult;

public sealed record AlterJournalCommandResult(string SystemId, string EntryId, int AlterId, bool Replay) : ICommandResult;

public sealed record FriendshipCommandResult(string SystemId, string TargetSystemId, string Action, bool Replay) : ICommandResult;

public sealed record SettingsCommandResult(string SystemId, string Action, bool Replay) : ICommandResult;

public sealed record SettingsFieldCommandResult(string SystemId, string Action, string FieldId, bool Replay) : ICommandResult;

public sealed record EncryptionCommandResult(string SystemId, string Action, string Key, bool Replay) : ICommandResult;