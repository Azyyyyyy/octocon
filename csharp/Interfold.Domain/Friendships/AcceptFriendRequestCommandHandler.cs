using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Friendships;

public sealed class AcceptFriendRequestCommandHandler : ICommandHandler<AcceptFriendRequestCommand, FriendshipCommandResult>
{
    private readonly IFriendshipRepository _repository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public AcceptFriendRequestCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _repository = repository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<FriendshipCommandResult>> HandleAsync(
        CommandEnvelope<AcceptFriendRequestCommand> command,
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
                return RejectDuplicate(command, "friend_request:accept");
            }

            var replay = CommandSerialization.Deserialize<FriendshipCommandResult>(previous.OutcomePayload);
            if (replay is not null)
            {
                return CommandExecutionResult<FriendshipCommandResult>.Success(replay with { Replay = true });
            }
        }

        var canonicalSourceSystemId = FriendshipIdNormalization.CanonicalizeForPrincipal(
            command.PrincipalId,
            command.Payload.SourceSystemId);

        var canonicalPrincipalId = FriendshipIdNormalization.CanonicalizeForPrincipal(
            command.Payload.SourceSystemId,
            command.PrincipalId);

        var outcome = await _repository.AcceptRequestAsync(
            command.PrincipalId,
            canonicalSourceSystemId,
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
            canonicalSourceSystemId,
            "accepted",
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

        await _eventBus.PublishAsync(new FriendshipAddedEvent(
            canonicalPrincipalId,
            canonicalSourceSystemId), cancellationToken);

        await _eventBus.PublishAsync(new FriendshipAddedEvent(
            canonicalSourceSystemId,
            canonicalPrincipalId), cancellationToken);

        await _eventBus.PublishAsync(new FriendRequestRemovedFromEvent(
            canonicalPrincipalId,
            canonicalSourceSystemId), cancellationToken);

        await _eventBus.PublishAsync(new FriendRequestRemovedToEvent(
            canonicalSourceSystemId,
            canonicalPrincipalId), cancellationToken);

        return CommandExecutionResult<FriendshipCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FriendshipCommandResult> RejectDuplicate(
        CommandEnvelope<AcceptFriendRequestCommand> command,
        string entityRef)
        => CommandExecutionResult<FriendshipCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<FriendshipCommandResult> RejectInvariant(
        CommandEnvelope<AcceptFriendRequestCommand> command,
        string entityRef)
        => CommandExecutionResult<FriendshipCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}
