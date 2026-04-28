using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Alters;

namespace Interfold.Domain.Journals;

public sealed class DetachAlterFromGlobalJournalCommandHandler : ICommandHandler<DetachAlterFromGlobalJournalCommand, GlobalJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IAlterRepository _alterRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public DetachAlterFromGlobalJournalCommandHandler(
        IJournalRepository journalRepository,
        IAlterRepository alterRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _journalRepository = journalRepository;
        _alterRepository = alterRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<GlobalJournalCommandResult>> HandleAsync(
        CommandEnvelope<DetachAlterFromGlobalJournalCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.AlterId is < 1 or > 32_767)
            return RejectInvariant(command, "alter:id");

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "journal:global:detach_alter");

            var replay = CommandSerialization.Deserialize<GlobalJournalCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<GlobalJournalCommandResult>.Success(replay with { Replay = true });
        }

        var alterExists = await _alterRepository.ExistsAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (!alterExists)
            return RejectInvariant(command, "journal:alter_not_found");

        var exists = await _journalRepository.ExistsGlobalAsync(command.PrincipalId, command.Payload.EntryId, cancellationToken);
        if (!exists)
            return RejectInvariant(command, "journal:not_found");

        var detached = await _journalRepository.DetachGlobalAlterAsync(
            command.PrincipalId, command.Payload.EntryId, command.Payload.AlterId, cancellationToken);
        if (!detached)
            return RejectInvariant(command, "journal:detach_failed");

        var result = new GlobalJournalCommandResult(command.PrincipalId, command.Payload.EntryId, Replay: false);
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

        await _eventBus.PublishAsync(new GlobalJournalEntryUpdatedEvent(command.PrincipalId, command.Payload.EntryId), cancellationToken);
        return CommandExecutionResult<GlobalJournalCommandResult>.Success(result);
    }

    private static CommandExecutionResult<GlobalJournalCommandResult> RejectDuplicate(
        CommandEnvelope<DetachAlterFromGlobalJournalCommand> command, string entityRef) =>
        CommandExecutionResult<GlobalJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<GlobalJournalCommandResult> RejectInvariant(
        CommandEnvelope<DetachAlterFromGlobalJournalCommand> command, string entityRef) =>
        CommandExecutionResult<GlobalJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}
