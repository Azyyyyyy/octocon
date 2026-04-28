using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Fronting;

public sealed class BulkUpdateFrontCommandHandler : ICommandHandler<BulkUpdateFrontCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public BulkUpdateFrontCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _frontingRepository = frontingRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<FrontCommandResult>> HandleAsync(
        CommandEnvelope<BulkUpdateFrontCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.Start.Any(x => x.AlterId is < 1 or > 32_767) ||
            command.Payload.End.Any(x => x is < 1 or > 32_767))
            return RejectInvariant(command, "fronting:invalid_alter_id");

        if (command.Payload.Start.Any(x => (x.Comment?.Length ?? 0) > 50))
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
                return RejectDuplicate(command, "fronting:bulk_update");

            var replay = CommandSerialization.Deserialize<FrontCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<FrontCommandResult>.Success(replay with { Replay = true });
        }

        foreach (var alterId in command.Payload.End)
        {
            await _frontingRepository.EndAsync(command.PrincipalId, alterId, cancellationToken);
        }

        foreach (var item in command.Payload.Start)
        {
            var alreadyFronting = await _frontingRepository.IsFrontingAsync(command.PrincipalId, item.AlterId, cancellationToken);
            if (!alreadyFronting)
                await _frontingRepository.StartAsync(command.PrincipalId, item.AlterId, item.Comment, cancellationToken);
        }

        var result = new FrontCommandResult(command.PrincipalId, null, null, Replay: false);
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

        // Emit granular event for socket layer to handle fronting_bulk
        await _eventBus.PublishAsync(new FrontingBulkUpdatedEvent(command.PrincipalId), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FrontCommandResult> RejectDuplicate(
        CommandEnvelope<BulkUpdateFrontCommand> command,
        string entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<FrontCommandResult> RejectInvariant(
        CommandEnvelope<BulkUpdateFrontCommand> command,
        string entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}