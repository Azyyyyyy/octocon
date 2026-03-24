using Octocon.Contracts.Operations;
using Octocon.Domain.Abstractions;

namespace Octocon.Domain.Journals;

public sealed class CreateGlobalJournalEntryCommandHandler : ICommandHandler<CreateGlobalJournalEntryCommand, GlobalJournalCommandResult>
{
    private const string AggregateType = "journals";

    private readonly IJournalRepository _journalRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public CreateGlobalJournalEntryCommandHandler(
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

    public async Task<CommandExecutionResult<GlobalJournalCommandResult>> HandleAsync(
        CommandEnvelope<CreateGlobalJournalEntryCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Title))
            return RejectInvariant(command, "journal:title_required");

        if (command.Payload.Title.Length > 250)
            return RejectInvariant(command, "journal:title_too_long");

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "journal:global:create");

            var replay = CommandSerialization.Deserialize<GlobalJournalCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<GlobalJournalCommandResult>.Success(replay with { Replay = true });
        }

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType, command.PrincipalId, command.ExpectedVersion, cancellationToken);
        if (!versionAdvanced)
            return await RejectStaleVersion(command, cancellationToken);

        var entryId = await _journalRepository.CreateGlobalAsync(command.PrincipalId, command.Payload, cancellationToken);
        if (entryId is null)
            return RejectInvariant(command, "journal:create_failed");

        var result = new GlobalJournalCommandResult(command.PrincipalId, entryId, Replay: false);
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

        await _eventBus.PublishAsync(new GlobalJournalChangedEvent(command.PrincipalId, "global_journal_entry_created", entryId), cancellationToken);
        return CommandExecutionResult<GlobalJournalCommandResult>.Success(result);
    }

    private static CommandExecutionResult<GlobalJournalCommandResult> RejectDuplicate(
        CommandEnvelope<CreateGlobalJournalEntryCommand> command, string entityRef) =>
        CommandExecutionResult<GlobalJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<GlobalJournalCommandResult> RejectInvariant(
        CommandEnvelope<CreateGlobalJournalEntryCommand> command, string entityRef) =>
        CommandExecutionResult<GlobalJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<GlobalJournalCommandResult>> RejectStaleVersion(
        CommandEnvelope<CreateGlobalJournalEntryCommand> command, CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<GlobalJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId,
                $"{AggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
    }
}
