using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Tags;

public sealed class UpdateTagCommandHandler : ICommandHandler<UpdateTagCommand, TagCommandResult>
{
    private readonly ITagRepository _tagRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public UpdateTagCommandHandler(
        ITagRepository tagRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _tagRepository = tagRepository;
        _idempotencyStore = idempotencyStore;
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
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<TagCommandResult> RejectInvariant(
        CommandEnvelope<UpdateTagCommand> command, string entityRef) =>
        CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}
