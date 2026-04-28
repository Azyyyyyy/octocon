using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Journals;

public sealed class DeleteAlterJournalEntryCommandHandler : ICommandHandler<DeleteAlterJournalEntryCommand, AlterJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public DeleteAlterJournalEntryCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _journalRepository = journalRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<AlterJournalCommandResult>> HandleAsync(
        CommandEnvelope<DeleteAlterJournalEntryCommand> command,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "journal:alter:delete");

            var replay = CommandSerialization.Deserialize<AlterJournalCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<AlterJournalCommandResult>.Success(replay with { Replay = true });
        }

        var alterRef = await _journalRepository.GetAlterRefAsync(command.PrincipalId, command.Payload.EntryId, cancellationToken);
        if (alterRef is null)
            return RejectInvariant(command, "journal:not_found");

        var deleted = await _journalRepository.DeleteAlterAsync(command.PrincipalId, command.Payload.EntryId, cancellationToken);
        if (!deleted)
            return RejectInvariant(command, "journal:delete_failed");

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

        await _eventBus.PublishAsync(new AlterJournalEntryDeletedEvent(command.PrincipalId, command.Payload.EntryId), cancellationToken);
        return CommandExecutionResult<AlterJournalCommandResult>.Success(result);
    }

    private static CommandExecutionResult<AlterJournalCommandResult> RejectDuplicate(
        CommandEnvelope<DeleteAlterJournalEntryCommand> command, string entityRef) =>
        CommandExecutionResult<AlterJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<AlterJournalCommandResult> RejectInvariant(
        CommandEnvelope<DeleteAlterJournalEntryCommand> command, string entityRef) =>
        CommandExecutionResult<AlterJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}
