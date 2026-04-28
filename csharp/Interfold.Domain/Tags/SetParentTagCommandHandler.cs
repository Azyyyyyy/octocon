using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Tags;

public sealed class SetParentTagCommandHandler : ICommandHandler<SetParentTagCommand, TagCommandResult>
{
    private readonly ITagRepository _tagRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public SetParentTagCommandHandler(
        ITagRepository tagRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _tagRepository = tagRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<TagCommandResult>> HandleAsync(
        CommandEnvelope<SetParentTagCommand> command,
        CancellationToken cancellationToken = default)
    {
        var payload = command.Payload;

        if (payload.TagId == payload.ParentTagId)
            return RejectInvariant(command, "tag:cycle");

        var payloadJson = CommandSerialization.Serialize(payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "tag:set_parent");

            var replay = CommandSerialization.Deserialize<TagCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<TagCommandResult>.Success(replay with { Replay = true });
        }

        // Cycle detection: walk up from the proposed parent — if we encounter TagId, it's a cycle.
        if (await WouldCreateCycleAsync(command.PrincipalId, payload.ParentTagId, payload.TagId, cancellationToken))
            return RejectInvariant(command, "tag:cycle");

        // SetParentAsync returns false if tag or parent tag does not exist.
        var set = await _tagRepository.SetParentAsync(
            command.PrincipalId, payload.TagId, payload.ParentTagId, cancellationToken);

        if (!set) return RejectInvariant(command, "tag:not_found");

        var result = new TagCommandResult(command.PrincipalId, payload.TagId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey,
            payloadHash, CommandSerialization.Hash(resultJson), resultJson, cancellationToken);

        await _eventBus.PublishAsync(
            new TagUpdatedEvent(command.PrincipalId, payload.TagId),
            cancellationToken);

        return CommandExecutionResult<TagCommandResult>.Success(result);
    }

    /// <summary>
    /// Walks up the ancestor chain starting at <paramref name="candidateParentId"/>.
    /// Returns true if <paramref name="childId"/> appears, indicating a cycle.
    /// </summary>
    private async Task<bool> WouldCreateCycleAsync(
        string systemId, string candidateParentId, string childId, CancellationToken cancellationToken)
    {
        var current = candidateParentId;
        while (current is not null)
        {
            if (current == childId) return true;
            current = await _tagRepository.GetParentIdAsync(systemId, current, cancellationToken);
        }
        return false;
    }

    private static CommandExecutionResult<TagCommandResult> RejectDuplicate(
        CommandEnvelope<SetParentTagCommand> command, string entityRef) =>
        CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<TagCommandResult> RejectInvariant(
        CommandEnvelope<SetParentTagCommand> command, string entityRef) =>
        CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}
