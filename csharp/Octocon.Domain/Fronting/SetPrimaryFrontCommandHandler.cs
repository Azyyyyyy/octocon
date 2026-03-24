using Octocon.Contracts.Operations;
using Octocon.Domain.Abstractions;

namespace Octocon.Domain.Fronting;

public sealed class SetPrimaryFrontCommandHandler : ICommandHandler<SetPrimaryFrontCommand, FrontCommandResult>
{
    private const string AggregateType = "fronting";

    private readonly IFrontingRepository _frontingRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public SetPrimaryFrontCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore,
        IClusterEventBus eventBus
    )
    {
        _frontingRepository = frontingRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<FrontCommandResult>> HandleAsync(
        CommandEnvelope<SetPrimaryFrontCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (command.Payload.AlterId is < 1 or > 32_767)
        {
            return RejectInvariant(command, "fronting:invalid_alter_id");
        }

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            cancellationToken
        );

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return RejectDuplicate(command, "fronting:primary");
            }

            var replay = CommandSerialization.Deserialize<FrontCommandResult>(previous.OutcomePayload);
            if (replay is not null)
            {
                return CommandExecutionResult<FrontCommandResult>.Success(replay with { Replay = true });
            }
        }

        if (command.Payload.AlterId is int alterId)
        {
            var fronting = await _frontingRepository.IsFrontingAsync(command.PrincipalId, alterId, cancellationToken);
            if (!fronting)
            {
                return RejectInvariant(command, "fronting:not_fronting");
            }
        }

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType,
            command.PrincipalId,
            command.ExpectedVersion,
            cancellationToken
        );

        if (!versionAdvanced)
        {
            return await RejectStaleVersion(command, cancellationToken);
        }

        var set = await _frontingRepository.SetPrimaryAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (!set)
        {
            return RejectInvariant(command, "fronting:primary_failed");
        }

        var result = new FrontCommandResult(command.PrincipalId, command.Payload.AlterId, FrontId: null, Replay: false);
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

        await _eventBus.PublishAsync(new FrontingStateChangedEvent(command.PrincipalId), cancellationToken);
        await _eventBus.PublishAsync(new FrontingPrimaryChangedEvent(command.PrincipalId, command.Payload.AlterId), cancellationToken);
        await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, false), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FrontCommandResult> RejectDuplicate(
        CommandEnvelope<SetPrimaryFrontCommand> command,
        string entityRef
    ) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictDuplicate,
                command.OperationId,
                entityRef,
                null,
                "no_retry",
                null
            )
        );

    private static CommandExecutionResult<FrontCommandResult> RejectInvariant(
        CommandEnvelope<SetPrimaryFrontCommand> command,
        string entityRef
    ) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictInvariant,
                command.OperationId,
                entityRef,
                null,
                "manual_merge_required",
                null
            )
        );

    private async Task<CommandExecutionResult<FrontCommandResult>> RejectStaleVersion(
        CommandEnvelope<SetPrimaryFrontCommand> command,
        CancellationToken cancellationToken
    )
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictStaleVersion,
                command.OperationId,
                $"{AggregateType}:{command.PrincipalId}",
                current,
                "refresh_and_retry",
                null
            )
        );
    }
}