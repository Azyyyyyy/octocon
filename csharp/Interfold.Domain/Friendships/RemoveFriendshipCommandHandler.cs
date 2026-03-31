using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Friendships;

public sealed class RemoveFriendshipCommandHandler : ICommandHandler<RemoveFriendshipCommand, FriendshipCommandResult>
{
    private const string AggregateType = "friendships";

    private readonly IFriendshipRepository _repository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public RemoveFriendshipCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore,
        IClusterEventBus eventBus)
    {
        _repository = repository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<FriendshipCommandResult>> HandleAsync(
        CommandEnvelope<RemoveFriendshipCommand> command,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return RejectDuplicate(command, "friendship:remove");
            }

            var replay = CommandSerialization.Deserialize<FriendshipCommandResult>(previous.OutcomePayload);
            if (replay is not null)
            {
                return CommandExecutionResult<FriendshipCommandResult>.Success(replay with { Replay = true });
            }
        }

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType, command.PrincipalId, command.ExpectedVersion, cancellationToken);

        if (!versionAdvanced)
        {
            return await RejectStaleVersion(command, cancellationToken);
        }

        var deleted = await _repository.RemoveFriendshipAsync(
            command.PrincipalId,
            command.Payload.FriendSystemId,
            cancellationToken);

        if (!deleted)
        {
            return RejectInvariant(command, "friendship:not_found");
        }

        var result = new FriendshipCommandResult(
            command.PrincipalId,
            command.Payload.FriendSystemId,
            "removed",
            Replay: false);

        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        await _eventBus.PublishAsync(new FriendshipRemovedEvent(
            command.PrincipalId,
            command.Payload.FriendSystemId), cancellationToken);

        await _eventBus.PublishAsync(new FriendshipRemovedEvent(
            command.Payload.FriendSystemId,
            command.PrincipalId), cancellationToken);

        return CommandExecutionResult<FriendshipCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FriendshipCommandResult> RejectDuplicate(
        CommandEnvelope<RemoveFriendshipCommand> command,
        string entityRef)
        => CommandExecutionResult<FriendshipCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<FriendshipCommandResult> RejectInvariant(
        CommandEnvelope<RemoveFriendshipCommand> command,
        string entityRef)
        => CommandExecutionResult<FriendshipCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<FriendshipCommandResult>> RejectStaleVersion(
        CommandEnvelope<RemoveFriendshipCommand> command,
        CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<FriendshipCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictStaleVersion,
                command.OperationId,
                $"{AggregateType}:{command.PrincipalId}",
                current,
                "refresh_and_retry",
                null));
    }
}
