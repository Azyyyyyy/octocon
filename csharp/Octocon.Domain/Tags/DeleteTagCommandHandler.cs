using Octocon.Contracts.Operations;
using Octocon.Domain.Abstractions;

namespace Octocon.Domain.Tags;

public sealed class DeleteTagCommandHandler : ICommandHandler<DeleteTagCommand, TagCommandResult>
{
    private const string AggregateType = "tags";

    private readonly ITagRepository _tagRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public DeleteTagCommandHandler(
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
        CommandEnvelope<DeleteTagCommand> command,
        CancellationToken cancellationToken = default)
    {
        var tagId = command.Payload.TagId;

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "tag:delete");

            var replay = CommandSerialization.Deserialize<TagCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<TagCommandResult>.Success(replay with { Replay = true });
        }

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType, command.PrincipalId, command.ExpectedVersion, cancellationToken);

        if (!versionAdvanced) return await RejectStaleVersion(command, cancellationToken);

        var found = await _tagRepository.DeleteAsync(command.PrincipalId, tagId, cancellationToken);
        if (!found) return RejectInvariant(command, "tag:not_found");

        var result = new TagCommandResult(command.PrincipalId, tagId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey,
            payloadHash, CommandSerialization.Hash(resultJson), resultJson, cancellationToken);

        await _eventBus.PublishAsync(
            new TagChangedEvent(command.PrincipalId, "tag_deleted", tagId),
            cancellationToken);

        return CommandExecutionResult<TagCommandResult>.Success(result);
    }

    private static CommandExecutionResult<TagCommandResult> RejectDuplicate(
        CommandEnvelope<DeleteTagCommand> command, string entityRef) =>
        CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<TagCommandResult> RejectInvariant(
        CommandEnvelope<DeleteTagCommand> command, string entityRef) =>
        CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<TagCommandResult>> RejectStaleVersion(
        CommandEnvelope<DeleteTagCommand> command, CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId,
                $"{AggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
    }
}
