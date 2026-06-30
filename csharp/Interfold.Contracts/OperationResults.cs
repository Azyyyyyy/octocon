namespace Interfold.Contracts;

public sealed record AccountCommandResult(string SystemId, string Username, bool Replay) : ICommandResult;

public sealed record AlterCommandResult(string SystemId, int AlterId, bool Replay) : ICommandResult;

public sealed record FrontCommandResult(string SystemId, int? AlterId, string? FrontId, bool Replay) : ICommandResult;

public sealed record TagCommandResult(string SystemId, string TagId, bool Replay) : ICommandResult;

public sealed record PollCommandResult(string SystemId, string PollId, bool Replay) : ICommandResult;

public sealed record GlobalJournalCommandResult(string SystemId, string EntryId, bool Replay) : ICommandResult;

public sealed record AlterJournalCommandResult(string SystemId, string EntryId, int AlterId, bool Replay) : ICommandResult;

public sealed record FriendshipCommandResult(string SystemId, string TargetSystemId, string Action, bool Replay) : ICommandResult;

public sealed record SettingsCommandResult(string SystemId, string Action, bool Replay) : ICommandResult;

/// <summary>
/// Result of dispatching an asynchronous third-party import (SP or PK) onto the in-process
/// worker queue. Returned by <c>ImportSpCommandHandler</c> / <c>ImportPkCommandHandler</c>
/// and serialised by the controller as the HTTP 202 Accepted body.
///
/// <para>
/// The <see cref="Status"/> field distinguishes the two dispatch outcomes:
/// </para>
/// <list type="bullet">
///   <item><c>"queued"</c> — this dispatch claimed a fresh per-system slot; a worker will pick it up.</item>
///   <item><c>"running"</c> — an import was already in flight for the same system; this dispatch collapsed onto the existing operation and the caller can subscribe to the same WebSocket completion frame.</item>
/// </list>
/// </summary>
public sealed record ImportDispatchCommandResult(
    string SystemId,
    Guid OperationId,
    string Kind,
    string Status,
    DateTimeOffset StartedAt,
    bool Replay) : ICommandResult;

public sealed record SettingsFieldCommandResult(string SystemId, string Action, string FieldId, bool Replay) : ICommandResult;

public sealed record EncryptionCommandResult(string SystemId, string Action, string Key, bool Replay) : ICommandResult;