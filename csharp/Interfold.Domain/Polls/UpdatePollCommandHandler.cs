using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Polls;

public sealed class UpdatePollCommandHandler : ICommandHandler<UpdatePollCommand, PollCommandResult>
{
    private const string AggregateType = "polls";

    private readonly IPollRepository _pollRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public UpdatePollCommandHandler(
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
        CommandEnvelope<UpdatePollCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.Title is null && command.Payload.Description is null &&
            command.Payload.TimeEndIso is null && command.Payload.DataJson is null)
            return RejectInvariant(command, "poll:no_fields");

        if (command.Payload.Title is not null && command.Payload.Title.Length > 100)
            return RejectInvariant(command, "poll:title_too_long");

        if (command.Payload.Description is not null && command.Payload.Description.Length > 2000)
            return RejectInvariant(command, "poll:description_too_long");

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "poll:update");

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

        var updated = await _pollRepository.UpdateAsync(command.PrincipalId, command.Payload, cancellationToken);
        if (!updated)
            return RejectInvariant(command, "poll:update_failed");

        var result = new PollCommandResult(command.PrincipalId, command.Payload.PollId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey,
            payloadHash, CommandSerialization.Hash(resultJson), resultJson, cancellationToken);

        await _eventBus.PublishAsync(new PollUpdatedEvent(command.PrincipalId, command.Payload.PollId), cancellationToken);
        return CommandExecutionResult<PollCommandResult>.Success(result);
    }

    private static CommandExecutionResult<PollCommandResult> RejectDuplicate(
        CommandEnvelope<UpdatePollCommand> command, string entityRef) =>
        CommandExecutionResult<PollCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<PollCommandResult> RejectInvariant(
        CommandEnvelope<UpdatePollCommand> command, string entityRef) =>
        CommandExecutionResult<PollCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<PollCommandResult>> RejectStaleVersion(
        CommandEnvelope<UpdatePollCommand> command, CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<PollCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId,
                $"{AggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
    }
}
