using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Fronting;

public sealed class UpdateFrontCommentCommandHandler : ICommandHandler<UpdateFrontCommentCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public UpdateFrontCommentCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _frontingRepository = frontingRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<FrontCommandResult>> HandleAsync(
        CommandEnvelope<UpdateFrontCommentCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.FrontId))
            return RejectInvariant(command, "fronting:invalid_front_id");

        if ((command.Payload.Comment?.Length ?? 0) > 50)
            return RejectInvariant(command, "fronting:invalid_comment");

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "fronting:update_comment");

            var replay = CommandSerialization.Deserialize<FrontCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<FrontCommandResult>.Success(replay with { Replay = true });
        }

        var existing = await _frontingRepository.GetActiveByFrontIdAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);
        if (existing is null)
            return RejectInvariant(command, "fronting:no_front");

        var updated = await _frontingRepository.UpdateCommentByFrontIdAsync(
            command.PrincipalId,
            command.Payload.FrontId,
            command.Payload.Comment ?? string.Empty,
            cancellationToken);

        if (!updated)
            return RejectInvariant(command, "fronting:update_comment_failed");

        var result = new FrontCommandResult(command.PrincipalId, existing.Front.AlterId, command.Payload.FrontId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        await _eventBus.PublishAsync(new FrontingStateChangedEvent(command.PrincipalId), cancellationToken);

        // Emit granular event for socket layer to handle front_updated
        await _eventBus.PublishAsync(new FrontCommentUpdatedEvent(command.PrincipalId, command.Payload.FrontId), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FrontCommandResult> RejectDuplicate(
        CommandEnvelope<UpdateFrontCommentCommand> command,
        string entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<FrontCommandResult> RejectInvariant(
        CommandEnvelope<UpdateFrontCommentCommand> command,
        string entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}
