using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Friendships;

public sealed class SetFriendTrustCommandHandler : ICommandHandler<SetFriendTrustCommand, FriendshipCommandResult>
{
    private const string AggregateType = "friendships";

    private readonly IFriendshipRepository _repository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public SetFriendTrustCommandHandler(
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
        CommandEnvelope<SetFriendTrustCommand> command,
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
                return RejectDuplicate(command, "friendship:trust");
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

        var updated = await _repository.SetTrustedAsync(
            command.PrincipalId,
            command.Payload.FriendSystemId,
            command.Payload.Trusted,
            cancellationToken);

        if (!updated)
        {
            return RejectInvariant(command, "friendship:not_found");
        }

        var result = new FriendshipCommandResult(
            command.PrincipalId,
            command.Payload.FriendSystemId,
            command.Payload.Trusted ? "trusted" : "untrusted",
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

        if (command.Payload.Trusted)
        {
            await _eventBus.PublishAsync(
                new FriendshipTrustedEvent(command.PrincipalId, command.Payload.FriendSystemId),
                cancellationToken);
        }
        else
        {
            await _eventBus.PublishAsync(
                new FriendshipUntrustedEvent(command.PrincipalId, command.Payload.FriendSystemId),
                cancellationToken);
        }

        return CommandExecutionResult<FriendshipCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FriendshipCommandResult> RejectDuplicate(
        CommandEnvelope<SetFriendTrustCommand> command,
        string entityRef)
        => CommandExecutionResult<FriendshipCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<FriendshipCommandResult> RejectInvariant(
        CommandEnvelope<SetFriendTrustCommand> command,
        string entityRef)
        => CommandExecutionResult<FriendshipCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<FriendshipCommandResult>> RejectStaleVersion(
        CommandEnvelope<SetFriendTrustCommand> command,
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
