using Octocon.Contracts.Operations;
using Octocon.Domain.Abstractions;

namespace Octocon.Domain.Journals;

public sealed class SetAlterJournalLockedCommandHandler : ICommandHandler<SetAlterJournalLockedCommand, AlterJournalCommandResult>
{
    private const string AggregateType = "journals";

    private readonly IJournalRepository _journalRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public SetAlterJournalLockedCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore,
        IClusterEventBus eventBus)
    {
        _journalRepository = journalRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<AlterJournalCommandResult>> HandleAsync(
        CommandEnvelope<SetAlterJournalLockedCommand> command,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "journal:alter:set_locked");

            var replay = CommandSerialization.Deserialize<AlterJournalCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<AlterJournalCommandResult>.Success(replay with { Replay = true });
        }

        var alterRef = await _journalRepository.GetAlterRefAsync(command.PrincipalId, command.Payload.EntryId, cancellationToken);
        if (alterRef is null)
            return RejectInvariant(command, "journal:not_found");

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType, command.PrincipalId, command.ExpectedVersion, cancellationToken);
        if (!versionAdvanced)
            return await RejectStaleVersion(command, cancellationToken);

        var updated = await _journalRepository.SetAlterLockedAsync(
            command.PrincipalId, command.Payload.EntryId, command.Payload.Locked, cancellationToken);
        if (!updated)
            return RejectInvariant(command, "journal:update_failed");

        var result = new AlterJournalCommandResult(command.PrincipalId, command.Payload.EntryId, alterRef.AlterId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken
        );

        await _eventBus.PublishAsync(new AlterJournalEntryUpdatedEvent(command.PrincipalId, command.Payload.EntryId), cancellationToken);
        return CommandExecutionResult<AlterJournalCommandResult>.Success(result);
    }

    private static CommandExecutionResult<AlterJournalCommandResult> RejectDuplicate(
        CommandEnvelope<SetAlterJournalLockedCommand> command, string entityRef) =>
        CommandExecutionResult<AlterJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<AlterJournalCommandResult> RejectInvariant(
        CommandEnvelope<SetAlterJournalLockedCommand> command, string entityRef) =>
        CommandExecutionResult<AlterJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<AlterJournalCommandResult>> RejectStaleVersion(
        CommandEnvelope<SetAlterJournalLockedCommand> command, CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<AlterJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId,
                $"{AggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
    }
}

public sealed class SetAlterJournalPinnedCommandHandler : ICommandHandler<SetAlterJournalPinnedCommand, AlterJournalCommandResult>
{
    private const string AggregateType = "journals";

    private readonly IJournalRepository _journalRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public SetAlterJournalPinnedCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore,
        IClusterEventBus eventBus)
    {
        _journalRepository = journalRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<AlterJournalCommandResult>> HandleAsync(
        CommandEnvelope<SetAlterJournalPinnedCommand> command,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "journal:alter:set_pinned");

            var replay = CommandSerialization.Deserialize<AlterJournalCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<AlterJournalCommandResult>.Success(replay with { Replay = true });
        }

        var alterRef = await _journalRepository.GetAlterRefAsync(command.PrincipalId, command.Payload.EntryId, cancellationToken);
        if (alterRef is null)
            return RejectInvariant(command, "journal:not_found");

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType, command.PrincipalId, command.ExpectedVersion, cancellationToken);
        if (!versionAdvanced)
            return await RejectStaleVersion(command, cancellationToken);

        var updated = await _journalRepository.SetAlterPinnedAsync(
            command.PrincipalId, command.Payload.EntryId, command.Payload.Pinned, cancellationToken);
        if (!updated)
            return RejectInvariant(command, "journal:update_failed");

        var result = new AlterJournalCommandResult(command.PrincipalId, command.Payload.EntryId, alterRef.AlterId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken
        );

        await _eventBus.PublishAsync(new AlterJournalEntryUpdatedEvent(command.PrincipalId, command.Payload.EntryId), cancellationToken);
        return CommandExecutionResult<AlterJournalCommandResult>.Success(result);
    }

    private static CommandExecutionResult<AlterJournalCommandResult> RejectDuplicate(
        CommandEnvelope<SetAlterJournalPinnedCommand> command, string entityRef) =>
        CommandExecutionResult<AlterJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<AlterJournalCommandResult> RejectInvariant(
        CommandEnvelope<SetAlterJournalPinnedCommand> command, string entityRef) =>
        CommandExecutionResult<AlterJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<AlterJournalCommandResult>> RejectStaleVersion(
        CommandEnvelope<SetAlterJournalPinnedCommand> command, CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<AlterJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId,
                $"{AggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
    }
}
