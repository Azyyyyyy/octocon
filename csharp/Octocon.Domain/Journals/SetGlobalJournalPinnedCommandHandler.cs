using Octocon.Contracts.Operations;
using Octocon.Domain.Abstractions;

namespace Octocon.Domain.Journals;

public sealed class SetGlobalJournalPinnedCommandHandler : ICommandHandler<SetGlobalJournalPinnedCommand, GlobalJournalCommandResult>
{
    private const string AggregateType = "journals";

    private readonly IJournalRepository _journalRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;

    public SetGlobalJournalPinnedCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore)
    {
        _journalRepository = journalRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
    }

    public async Task<CommandExecutionResult<GlobalJournalCommandResult>> HandleAsync(
        CommandEnvelope<SetGlobalJournalPinnedCommand> command,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "journal:global:set_pinned");

            var replay = CommandSerialization.Deserialize<GlobalJournalCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<GlobalJournalCommandResult>.Success(replay with { Replay = true });
        }

        var exists = await _journalRepository.ExistsGlobalAsync(command.PrincipalId, command.Payload.EntryId, cancellationToken);
        if (!exists)
            return RejectInvariant(command, "journal:not_found");

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType, command.PrincipalId, command.ExpectedVersion, cancellationToken);
        if (!versionAdvanced)
            return await RejectStaleVersion(command, cancellationToken);

        var updated = await _journalRepository.SetGlobalPinnedAsync(
            command.PrincipalId, command.Payload.EntryId, command.Payload.Pinned, cancellationToken);
        if (!updated)
            return RejectInvariant(command, "journal:update_failed");

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

        return CommandExecutionResult<GlobalJournalCommandResult>.Success(result);
    }

    private static CommandExecutionResult<GlobalJournalCommandResult> RejectDuplicate(
        CommandEnvelope<SetGlobalJournalPinnedCommand> command, string entityRef) =>
        CommandExecutionResult<GlobalJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<GlobalJournalCommandResult> RejectInvariant(
        CommandEnvelope<SetGlobalJournalPinnedCommand> command, string entityRef) =>
        CommandExecutionResult<GlobalJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<GlobalJournalCommandResult>> RejectStaleVersion(
        CommandEnvelope<SetGlobalJournalPinnedCommand> command, CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<GlobalJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId,
                $"{AggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
    }
}
