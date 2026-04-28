using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Polls;

public sealed class CreatePollCommandHandler : ICommandHandler<CreatePollCommand, PollCommandResult>
{
    private readonly IPollRepository _pollRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public CreatePollCommandHandler(
        IPollRepository pollRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _pollRepository = pollRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<PollCommandResult>> HandleAsync(
        CommandEnvelope<CreatePollCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Title))
            return RejectInvariant(command, "poll:title_required");

        if (command.Payload.Title.Length > 100)
            return RejectInvariant(command, "poll:title_too_long");

        if (!string.IsNullOrWhiteSpace(command.Payload.Description) && command.Payload.Description.Length > 2000)
            return RejectInvariant(command, "poll:description_too_long");

        // Validate poll type: accept both Elixir names (single_choice, multiple_choice, approval) and legacy names (vote, choice)
        var validTypes = new[] { "single_choice", "vote", "multiple_choice", "choice", "approval" };
        if (!validTypes.Any(t => string.Equals(command.Payload.Type, t, StringComparison.OrdinalIgnoreCase)))
            return RejectInvariant(command, "poll:type_invalid");

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "poll:create");

            var replay = CommandSerialization.Deserialize<PollCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<PollCommandResult>.Success(replay with { Replay = true });
        }

        var pollId = await _pollRepository.CreateAsync(command.PrincipalId, command.Payload, cancellationToken);
        if (pollId is null)
            return RejectInvariant(command, "poll:create_failed");

        var result = new PollCommandResult(command.PrincipalId, pollId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey,
            payloadHash, CommandSerialization.Hash(resultJson), resultJson, cancellationToken);

        await _eventBus.PublishAsync(new PollCreatedEvent(command.PrincipalId, pollId), cancellationToken);
        return CommandExecutionResult<PollCommandResult>.Success(result);
    }

    private static CommandExecutionResult<PollCommandResult> RejectDuplicate(
        CommandEnvelope<CreatePollCommand> command, string entityRef) =>
        CommandExecutionResult<PollCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<PollCommandResult> RejectInvariant(
        CommandEnvelope<CreatePollCommand> command, string entityRef) =>
        CommandExecutionResult<PollCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}
