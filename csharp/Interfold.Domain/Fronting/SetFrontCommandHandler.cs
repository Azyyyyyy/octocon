using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Fronting;

public sealed class SetFrontCommandHandler : ICommandHandler<SetFrontCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public SetFrontCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _frontingRepository = frontingRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<FrontCommandResult>> HandleAsync(
        CommandEnvelope<SetFrontCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.AlterId is < 1 or > 32_767)
            return RejectInvariant(command, "fronting:invalid_alter_id");

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
                return RejectDuplicate(command, "fronting:set");

            var replay = CommandSerialization.Deserialize<FrontCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<FrontCommandResult>.Success(replay with { Replay = true });
        }

        var alreadyFronting = await _frontingRepository.IsFrontingAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (alreadyFronting)
            return RejectInvariant(command, "fronting:already_fronting");

        var active = await _frontingRepository.ListActiveAsync(command.PrincipalId, cancellationToken);
        foreach (var front in active)
            await _frontingRepository.EndAsync(command.PrincipalId, front.Front.AlterId, cancellationToken);

        var frontId = await _frontingRepository.StartAsync(
            command.PrincipalId,
            command.Payload.AlterId,
            command.Payload.Comment,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(frontId))
            return RejectInvariant(command, "fronting:start_failed");

        await _frontingRepository.SetPrimaryAsync(command.PrincipalId, null, cancellationToken);

        var result = new FrontCommandResult(command.PrincipalId, command.Payload.AlterId, frontId, Replay: false);
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

        // Emit granular event for socket layer to handle fronting_set
        await _eventBus.PublishAsync(new FrontingSetEvent(command.PrincipalId, frontId), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FrontCommandResult> RejectDuplicate(
        CommandEnvelope<SetFrontCommand> command,
        string entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<FrontCommandResult> RejectInvariant(
        CommandEnvelope<SetFrontCommand> command,
        string entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}