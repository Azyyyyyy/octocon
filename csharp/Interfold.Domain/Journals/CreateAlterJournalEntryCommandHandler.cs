using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Alters;

namespace Interfold.Domain.Journals;

public sealed class CreateAlterJournalEntryCommandHandler : ICommandHandler<CreateAlterJournalEntryCommand, AlterJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IAlterRepository _alterRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public CreateAlterJournalEntryCommandHandler(
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

    public async Task<CommandExecutionResult<AlterJournalCommandResult>> HandleAsync(
        CommandEnvelope<CreateAlterJournalEntryCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.AlterId is < 1 or > 32_767)
            return RejectInvariant(command, "alter:id");

        if (string.IsNullOrWhiteSpace(command.Payload.Title))
            return RejectInvariant(command, "journal:title_required");

        if (command.Payload.Title.Length > 100)
            return RejectInvariant(command, "journal:title_too_long");

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "journal:alter:create");

            var replay = CommandSerialization.Deserialize<AlterJournalCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<AlterJournalCommandResult>.Success(replay with { Replay = true });
        }

        var alterExists = await _alterRepository.ExistsAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (!alterExists)
            return RejectInvariant(command, "journal:alter_not_found");

        var entryId = await _journalRepository.CreateAlterAsync(command.PrincipalId, command.Payload, cancellationToken);
        if (entryId is null)
            return RejectInvariant(command, "journal:create_failed");

        var result = new AlterJournalCommandResult(command.PrincipalId, entryId, command.Payload.AlterId, Replay: false);
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

        await _eventBus.PublishAsync(new AlterJournalEntryCreatedEvent(command.PrincipalId, entryId), cancellationToken);
        return CommandExecutionResult<AlterJournalCommandResult>.Success(result);
    }

    private static CommandExecutionResult<AlterJournalCommandResult> RejectDuplicate(
        CommandEnvelope<CreateAlterJournalEntryCommand> command, string entityRef) =>
        CommandExecutionResult<AlterJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<AlterJournalCommandResult> RejectInvariant(
        CommandEnvelope<CreateAlterJournalEntryCommand> command, string entityRef) =>
        CommandExecutionResult<AlterJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}
