using Octocon.Contracts.Operations;
using Octocon.Domain.Abstractions;

namespace Octocon.Domain.Polls;

public sealed class DeletePollCommandHandler : ICommandHandler<DeletePollCommand, PollCommandResult>
{
    private const string AggregateType = "polls";

    private readonly IPollRepository _pollRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public DeletePollCommandHandler(
        IPollRepository pollRepository,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore,
        IClusterEventBus eventBus)
    {
        _pollRepository = pollRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<PollCommandResult>> HandleAsync(
        CommandEnvelope<DeletePollCommand> command,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "poll:delete");

            var replay = CommandSerialization.Deserialize<PollCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<PollCommandResult>.Success(replay with { Replay = true });
        }

        var exists = await _pollRepository.ExistsAsync(command.PrincipalId, command.Payload.PollId, cancellationToken);
        if (!exists)
            return RejectInvariant(command, "poll:not_found");

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType, command.PrincipalId, command.ExpectedVersion, cancellationToken);
        if (!versionAdvanced)
            return await RejectStaleVersion(command, cancellationToken);

        var deleted = await _pollRepository.DeleteAsync(command.PrincipalId, command.Payload.PollId, cancellationToken);
        if (!deleted)
            return RejectInvariant(command, "poll:delete_failed");

        var result = new PollCommandResult(command.PrincipalId, command.Payload.PollId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey,
            payloadHash, CommandSerialization.Hash(resultJson), resultJson, cancellationToken);

        await _eventBus.PublishAsync(new PollDeletedEvent(command.PrincipalId, command.Payload.PollId), cancellationToken);
        return CommandExecutionResult<PollCommandResult>.Success(result);
    }

    private static CommandExecutionResult<PollCommandResult> RejectDuplicate(
        CommandEnvelope<DeletePollCommand> command, string entityRef) =>
        CommandExecutionResult<PollCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<PollCommandResult> RejectInvariant(
        CommandEnvelope<DeletePollCommand> command, string entityRef) =>
        CommandExecutionResult<PollCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<PollCommandResult>> RejectStaleVersion(
        CommandEnvelope<DeletePollCommand> command, CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<PollCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId,
                $"{AggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
    }
}
