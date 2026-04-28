using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Journals;

public sealed class UpdateAlterJournalEntryCommandHandler : ICommandHandler<UpdateAlterJournalEntryCommand, AlterJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public UpdateAlterJournalEntryCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _journalRepository = journalRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<AlterJournalCommandResult>> HandleAsync(
        CommandEnvelope<UpdateAlterJournalEntryCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.Title is null && command.Payload.Content is null && command.Payload.Color is null)
            return RejectInvariant(command, "journal:no_fields");

        if (command.Payload.Title is not null && command.Payload.Title.Length > 100)
            return RejectInvariant(command, "journal:title_too_long");

        if (command.Payload.Content is not null && command.Payload.Content.Length > 50_000)
            return RejectInvariant(command, "journal:content_too_long");

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "journal:alter:update");

            var replay = CommandSerialization.Deserialize<AlterJournalCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<AlterJournalCommandResult>.Success(replay with { Replay = true });
        }

        var alterRef = await _journalRepository.GetAlterRefAsync(command.PrincipalId, command.Payload.EntryId, cancellationToken);
        if (alterRef is null)
            return RejectInvariant(command, "journal:not_found");

        var updated = await _journalRepository.UpdateAlterAsync(command.PrincipalId, command.Payload, cancellationToken);
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
        CommandEnvelope<UpdateAlterJournalEntryCommand> command, string entityRef) =>
        CommandExecutionResult<AlterJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<AlterJournalCommandResult> RejectInvariant(
        CommandEnvelope<UpdateAlterJournalEntryCommand> command, string entityRef) =>
        CommandExecutionResult<AlterJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}
