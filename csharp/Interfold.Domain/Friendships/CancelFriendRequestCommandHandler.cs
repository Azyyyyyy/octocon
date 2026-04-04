using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Friendships;

public sealed class CancelFriendRequestCommandHandler : ICommandHandler<CancelFriendRequestCommand, FriendshipCommandResult>
{
    private const string AggregateType = "friendships";

    private readonly IFriendshipRepository _repository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public CancelFriendRequestCommandHandler(
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
        CommandEnvelope<CancelFriendRequestCommand> command,
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
                return RejectDuplicate(command, "friend_request:cancel");
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

        var canonicalTargetSystemId = FriendshipIdNormalization.CanonicalizeForPrincipal(
            command.PrincipalId,
            command.Payload.TargetSystemId);

        var outcome = await _repository.CancelRequestAsync(
            command.PrincipalId,
            canonicalTargetSystemId,
            cancellationToken);

        if (outcome is FriendRequestMutationOutcome.AlreadyFriends)
        {
            return RejectInvariant(command, "friend_request:already_friends");
        }

        if (outcome is FriendRequestMutationOutcome.NotRequested)
        {
            return RejectInvariant(command, "friend_request:not_requested");
        }

        if (outcome is FriendRequestMutationOutcome.NoUser)
        {
            return RejectInvariant(command, "friend_request:no_user");
        }

        var result = new FriendshipCommandResult(
            command.PrincipalId,
            canonicalTargetSystemId,
            "cancelled",
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

        await _eventBus.PublishAsync(new FriendRequestRemovedToEvent(
            command.PrincipalId,
            canonicalTargetSystemId), cancellationToken);

        await _eventBus.PublishAsync(new FriendRequestRemovedFromEvent(
            canonicalTargetSystemId,
            command.PrincipalId), cancellationToken);

        return CommandExecutionResult<FriendshipCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FriendshipCommandResult> RejectDuplicate(
        CommandEnvelope<CancelFriendRequestCommand> command,
        string entityRef)
        => CommandExecutionResult<FriendshipCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<FriendshipCommandResult> RejectInvariant(
        CommandEnvelope<CancelFriendRequestCommand> command,
        string entityRef)
        => CommandExecutionResult<FriendshipCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<FriendshipCommandResult>> RejectStaleVersion(
        CommandEnvelope<CancelFriendRequestCommand> command,
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
