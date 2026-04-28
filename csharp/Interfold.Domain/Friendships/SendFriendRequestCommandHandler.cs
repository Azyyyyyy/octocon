using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Friendships;

public sealed class SendFriendRequestCommandHandler : ICommandHandler<SendFriendRequestCommand, FriendshipCommandResult>
{
    private readonly IFriendshipRepository _repository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public SendFriendRequestCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _repository = repository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<FriendshipCommandResult>> HandleAsync(
        CommandEnvelope<SendFriendRequestCommand> command,
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
                return RejectDuplicate(command, "friend_request:send");
            }

            var replay = CommandSerialization.Deserialize<FriendshipCommandResult>(previous.OutcomePayload);
            if (replay is not null)
            {
                return CommandExecutionResult<FriendshipCommandResult>.Success(replay with { Replay = true });
            }
        }

        var resolvedTargetSystemId = await _repository.ResolveUserIdAsync(
            command.Payload.TargetSystemId,
            cancellationToken);

        if (resolvedTargetSystemId is null)
        {
            return RejectInvariant(command, "friend_request:no_user");
        }

        var outcome = await _repository.SendRequestAsync(
            command.PrincipalId,
            resolvedTargetSystemId,
            cancellationToken);

        if (outcome is SendFriendRequestOutcome.AlreadyFriends)
        {
            return RejectInvariant(command, "friend_request:already_friends");
        }

        if (outcome is SendFriendRequestOutcome.AlreadySent)
        {
            return RejectInvariant(command, "friend_request:already_sent");
        }

        if (outcome is SendFriendRequestOutcome.NoUser)
        {
            return RejectInvariant(command, "friend_request:no_user");
        }

        var action = outcome is SendFriendRequestOutcome.Accepted ? "accepted" : "sent";

        var result = new FriendshipCommandResult(
            command.PrincipalId,
            resolvedTargetSystemId,
            action,
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

        //TODO: Check this path sends the required events
        if (outcome is SendFriendRequestOutcome.Accepted)
        {
            await _eventBus.PublishAsync(new FriendshipAddedEvent(
                command.PrincipalId,
                resolvedTargetSystemId), cancellationToken);

            await _eventBus.PublishAsync(new FriendshipAddedEvent(
                resolvedTargetSystemId,
                command.PrincipalId), cancellationToken);

            await _eventBus.PublishAsync(new FriendRequestRemovedToEvent(
                resolvedTargetSystemId,
                command.PrincipalId), cancellationToken);
        }
        else
        {
            await _eventBus.PublishAsync(new FriendRequestSentEvent(
                command.PrincipalId,
                resolvedTargetSystemId), cancellationToken);

            await _eventBus.PublishAsync(new FriendRequestReceivedEvent(
                resolvedTargetSystemId,
                command.PrincipalId), cancellationToken);
        }

        return CommandExecutionResult<FriendshipCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FriendshipCommandResult> RejectDuplicate(
        CommandEnvelope<SendFriendRequestCommand> command,
        string entityRef)
        => CommandExecutionResult<FriendshipCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<FriendshipCommandResult> RejectInvariant(
        CommandEnvelope<SendFriendRequestCommand> command,
        string entityRef)
        => CommandExecutionResult<FriendshipCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}
