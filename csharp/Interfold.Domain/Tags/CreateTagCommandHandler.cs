using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Tags;

public sealed class CreateTagCommandHandler : ICommandHandler<CreateTagCommand, TagCommandResult>
{
    private readonly ITagRepository _tagRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public CreateTagCommandHandler(
        ITagRepository tagRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
    {
        _tagRepository = tagRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<TagCommandResult>> HandleAsync(
        CommandEnvelope<CreateTagCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Name))
            return RejectInvariant(command, "tag:name_required");

        if (command.Payload.Name.Length > 50)
            return RejectInvariant(command, "tag:name_too_long");

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            cancellationToken
        );

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "tag:create");

            var replay = CommandSerialization.Deserialize<TagCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<TagCommandResult>.Success(replay with { Replay = true });
        }

        if (!string.IsNullOrWhiteSpace(command.Payload.ParentTagId))
        {
            var parentExists = await _tagRepository.ExistsAsync(
                command.PrincipalId,
                command.Payload.ParentTagId,
                cancellationToken
            );

            if (!parentExists)
                return RejectInvariant(command, "tag:parent_not_found");
        }

        var tagId = await _tagRepository.CreateAsync(command.PrincipalId, command.Payload, cancellationToken);
        if (tagId is null)
            return RejectInvariant(command, "tag:create_failed");

        var result = new TagCommandResult(command.PrincipalId, tagId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken
        );

        await _eventBus.PublishAsync(
            new TagCreatedEvent(command.PrincipalId, tagId),
            cancellationToken);

        return CommandExecutionResult<TagCommandResult>.Success(result);
    }

    private static CommandExecutionResult<TagCommandResult> RejectDuplicate(
        CommandEnvelope<CreateTagCommand> command,
        string entityRef
    ) =>
        CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry")
        );

    private static CommandExecutionResult<TagCommandResult> RejectInvariant(
        CommandEnvelope<CreateTagCommand> command,
        string entityRef
    ) =>
        CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required")
        );
}
