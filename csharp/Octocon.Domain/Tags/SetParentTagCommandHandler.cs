using Octocon.Contracts.Operations;
using Octocon.Domain.Abstractions;

namespace Octocon.Domain.Tags;

public sealed class SetParentTagCommandHandler : ICommandHandler<SetParentTagCommand, TagCommandResult>
{
    private const string AggregateType = "tags";

    private readonly ITagRepository _tagRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public SetParentTagCommandHandler(
        ITagRepository tagRepository,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore,
        IClusterEventBus eventBus)
    {
        _tagRepository = tagRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
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

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType, command.PrincipalId, command.ExpectedVersion, cancellationToken);

        if (!versionAdvanced) return await RejectStaleVersion(command, cancellationToken);

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
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<TagCommandResult> RejectInvariant(
        CommandEnvelope<SetParentTagCommand> command, string entityRef) =>
        CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<TagCommandResult>> RejectStaleVersion(
        CommandEnvelope<SetParentTagCommand> command, CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId,
                $"{AggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
    }
}
