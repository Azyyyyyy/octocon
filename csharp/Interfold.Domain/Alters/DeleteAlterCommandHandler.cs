using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Alters;

public sealed class DeleteAlterCommandHandler : ICommandHandler<DeleteAlterCommand, AlterCommandResult>
{
    private const string AggregateType = "alters";

    private readonly IAlterRepository _alterRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public DeleteAlterCommandHandler(
        IAlterRepository alterRepository,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore,
        IClusterEventBus eventBus)
    {
        _alterRepository = alterRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<AlterCommandResult>> HandleAsync(
        CommandEnvelope<DeleteAlterCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.AlterId is < 1 or > 32_767)
            return RejectInvariant(command, "alter:id");

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
                return RejectDuplicate(command, "alter:delete");

            var replay = CommandSerialization.Deserialize<AlterCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<AlterCommandResult>.Success(replay with { Replay = true });
        }

        var exists = await _alterRepository.ExistsAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (!exists)
            return RejectInvariant(command, "alter:not_found");

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType,
            command.PrincipalId,
            command.ExpectedVersion,
            cancellationToken
        );

        if (!versionAdvanced)
            return await RejectStaleVersion(command, cancellationToken);

        var deleted = await _alterRepository.DeleteAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (!deleted)
            return RejectInvariant(command, "alter:delete_failed");

        var result = new AlterCommandResult(command.PrincipalId, command.Payload.AlterId, Replay: false);
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

        await _eventBus.PublishAsync(
            new AlterDeletedEvent(command.PrincipalId, command.Payload.AlterId),
            cancellationToken);

        return CommandExecutionResult<AlterCommandResult>.Success(result);
    }

    private static CommandExecutionResult<AlterCommandResult> RejectDuplicate(
        CommandEnvelope<DeleteAlterCommand> command,
        string entityRef
    ) =>
        CommandExecutionResult<AlterCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictDuplicate,
                command.OperationId,
                entityRef,
                null,
                "no_retry",
                null
            )
        );

    private static CommandExecutionResult<AlterCommandResult> RejectInvariant(
        CommandEnvelope<DeleteAlterCommand> command,
        string entityRef
    ) =>
        CommandExecutionResult<AlterCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictInvariant,
                command.OperationId,
                entityRef,
                null,
                "manual_merge_required",
                null
            )
        );

    private async Task<CommandExecutionResult<AlterCommandResult>> RejectStaleVersion(
        CommandEnvelope<DeleteAlterCommand> command,
        CancellationToken cancellationToken
    )
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<AlterCommandResult>.Rejected(
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
