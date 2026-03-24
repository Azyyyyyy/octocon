using Octocon.Contracts.Operations;
using Octocon.Domain.Abstractions;

namespace Octocon.Domain.Tags;

public sealed class UpdateTagCommandHandler : ICommandHandler<UpdateTagCommand, TagCommandResult>
{
    private const string AggregateType = "tags";

    private readonly ITagRepository _tagRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public UpdateTagCommandHandler(
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
        CommandEnvelope<UpdateTagCommand> command,
        CancellationToken cancellationToken = default)
    {
        var payload = command.Payload;

        if (payload.Name is null && payload.Color is null && payload.Description is null && payload.SecurityLevel is null)
            return RejectInvariant(command, "tag:no_fields");

        if (payload.Name is not null && payload.Name.Length > 50)
            return RejectInvariant(command, "tag:name_too_long");

        var payloadJson = CommandSerialization.Serialize(payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "tag:update");

            var replay = CommandSerialization.Deserialize<TagCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<TagCommandResult>.Success(replay with { Replay = true });
        }

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType, command.PrincipalId, command.ExpectedVersion, cancellationToken);

        if (!versionAdvanced) return await RejectStaleVersion(command, cancellationToken);

        var found = await _tagRepository.UpdateAsync(command.PrincipalId, payload, cancellationToken);
        if (!found) return RejectInvariant(command, "tag:not_found");

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

    private static CommandExecutionResult<TagCommandResult> RejectDuplicate(
        CommandEnvelope<UpdateTagCommand> command, string entityRef) =>
        CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<TagCommandResult> RejectInvariant(
        CommandEnvelope<UpdateTagCommand> command, string entityRef) =>
        CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<TagCommandResult>> RejectStaleVersion(
        CommandEnvelope<UpdateTagCommand> command, CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId,
                $"{AggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
    }
}
