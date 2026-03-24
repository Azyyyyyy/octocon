using Octocon.Contracts.Operations;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Alters;

namespace Octocon.Domain.Journals;

public sealed class AttachAlterToGlobalJournalCommandHandler : ICommandHandler<AttachAlterToGlobalJournalCommand, GlobalJournalCommandResult>
{
    private const string AggregateType = "journals";

    private readonly IJournalRepository _journalRepository;
    private readonly IAlterRepository _alterRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public AttachAlterToGlobalJournalCommandHandler(
        IJournalRepository journalRepository,
        IAlterRepository alterRepository,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore,
        IClusterEventBus eventBus)
    {
        _journalRepository = journalRepository;
        _alterRepository = alterRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<GlobalJournalCommandResult>> HandleAsync(
        CommandEnvelope<AttachAlterToGlobalJournalCommand> command,
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
                return RejectDuplicate(command, "journal:global:attach_alter");

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

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType, command.PrincipalId, command.ExpectedVersion, cancellationToken);
        if (!versionAdvanced)
            return await RejectStaleVersion(command, cancellationToken);

        var attached = await _journalRepository.AttachGlobalAlterAsync(
            command.PrincipalId, command.Payload.EntryId, command.Payload.AlterId, cancellationToken);
        if (!attached)
            return RejectInvariant(command, "journal:attach_failed");

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
        CommandEnvelope<AttachAlterToGlobalJournalCommand> command, string entityRef) =>
        CommandExecutionResult<GlobalJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<GlobalJournalCommandResult> RejectInvariant(
        CommandEnvelope<AttachAlterToGlobalJournalCommand> command, string entityRef) =>
        CommandExecutionResult<GlobalJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<GlobalJournalCommandResult>> RejectStaleVersion(
        CommandEnvelope<AttachAlterToGlobalJournalCommand> command, CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<GlobalJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId,
                $"{AggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
    }
}
