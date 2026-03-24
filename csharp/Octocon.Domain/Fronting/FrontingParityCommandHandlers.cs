using Octocon.Contracts.Operations;
using Octocon.Domain.Abstractions;

namespace Octocon.Domain.Fronting;

public sealed class BulkUpdateFrontCommandHandler : ICommandHandler<BulkUpdateFrontCommand, FrontCommandResult>
{
    private const string AggregateType = "fronting";

    private readonly IFrontingRepository _frontingRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public BulkUpdateFrontCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore,
        IClusterEventBus eventBus)
    {
        _frontingRepository = frontingRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<FrontCommandResult>> HandleAsync(
        CommandEnvelope<BulkUpdateFrontCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.Start.Any(x => x.AlterId is < 1 or > 32_767) ||
            command.Payload.End.Any(x => x is < 1 or > 32_767))
            return RejectInvariant(command, "fronting:invalid_alter_id");

        if (command.Payload.Start.Any(x => (x.Comment?.Length ?? 0) > 50))
            return RejectInvariant(command, "fronting:invalid_comment");

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "fronting:bulk_update");

            var replay = CommandSerialization.Deserialize<FrontCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<FrontCommandResult>.Success(replay with { Replay = true });
        }

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType,
            command.PrincipalId,
            command.ExpectedVersion,
            cancellationToken);

        if (!versionAdvanced)
            return await RejectStaleVersion(command, cancellationToken);

        foreach (var alterId in command.Payload.End)
        {
            await _frontingRepository.EndAsync(command.PrincipalId, alterId, cancellationToken);
        }

        foreach (var item in command.Payload.Start)
        {
            var alreadyFronting = await _frontingRepository.IsFrontingAsync(command.PrincipalId, item.AlterId, cancellationToken);
            if (!alreadyFronting)
                await _frontingRepository.StartAsync(command.PrincipalId, item.AlterId, item.Comment, cancellationToken);
        }

        var result = new FrontCommandResult(command.PrincipalId, null, null, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        await _eventBus.PublishAsync(new FrontingStateChangedEvent(command.PrincipalId), cancellationToken);

        // Emit granular event for socket layer to handle fronting_bulk
        await _eventBus.PublishAsync(new FrontingBulkUpdatedEvent(command.PrincipalId), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FrontCommandResult> RejectDuplicate(
        CommandEnvelope<BulkUpdateFrontCommand> command,
        string entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<FrontCommandResult> RejectInvariant(
        CommandEnvelope<BulkUpdateFrontCommand> command,
        string entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<FrontCommandResult>> RejectStaleVersion(
        CommandEnvelope<BulkUpdateFrontCommand> command,
        CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId, $"{AggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
    }
}

public sealed class SetFrontCommandHandler : ICommandHandler<SetFrontCommand, FrontCommandResult>
{
    private const string AggregateType = "fronting";

    private readonly IFrontingRepository _frontingRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public SetFrontCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore,
        IClusterEventBus eventBus)
    {
        _frontingRepository = frontingRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<FrontCommandResult>> HandleAsync(
        CommandEnvelope<SetFrontCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.AlterId is < 1 or > 32_767)
            return RejectInvariant(command, "fronting:invalid_alter_id");

        if ((command.Payload.Comment?.Length ?? 0) > 50)
            return RejectInvariant(command, "fronting:invalid_comment");

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "fronting:set");

            var replay = CommandSerialization.Deserialize<FrontCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<FrontCommandResult>.Success(replay with { Replay = true });
        }

        var alreadyFronting = await _frontingRepository.IsFrontingAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (alreadyFronting)
            return RejectInvariant(command, "fronting:already_fronting");

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType,
            command.PrincipalId,
            command.ExpectedVersion,
            cancellationToken);

        if (!versionAdvanced)
            return await RejectStaleVersion(command, cancellationToken);

        var active = await _frontingRepository.ListActiveAsync(command.PrincipalId, cancellationToken);
        foreach (var front in active)
            await _frontingRepository.EndAsync(command.PrincipalId, front.Front.AlterId, cancellationToken);

        var frontId = await _frontingRepository.StartAsync(
            command.PrincipalId,
            command.Payload.AlterId,
            command.Payload.Comment,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(frontId))
            return RejectInvariant(command, "fronting:start_failed");

        await _frontingRepository.SetPrimaryAsync(command.PrincipalId, null, cancellationToken);

        var result = new FrontCommandResult(command.PrincipalId, command.Payload.AlterId, frontId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        await _eventBus.PublishAsync(new FrontingStateChangedEvent(command.PrincipalId), cancellationToken);

        // Emit granular event for socket layer to handle fronting_set
        await _eventBus.PublishAsync(new FrontingSetEvent(command.PrincipalId, frontId), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FrontCommandResult> RejectDuplicate(
        CommandEnvelope<SetFrontCommand> command,
        string entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<FrontCommandResult> RejectInvariant(
        CommandEnvelope<SetFrontCommand> command,
        string entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<FrontCommandResult>> RejectStaleVersion(
        CommandEnvelope<SetFrontCommand> command,
        CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId, $"{AggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
    }
}

public sealed class DeleteFrontByIdCommandHandler : ICommandHandler<DeleteFrontByIdCommand, FrontCommandResult>
{
    private const string AggregateType = "fronting";

    private readonly IFrontingRepository _frontingRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public DeleteFrontByIdCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore,
        IClusterEventBus eventBus)
    {
        _frontingRepository = frontingRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<FrontCommandResult>> HandleAsync(
        CommandEnvelope<DeleteFrontByIdCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.FrontId))
            return RejectInvariant(command, "fronting:invalid_front_id");

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "fronting:delete");

            var replay = CommandSerialization.Deserialize<FrontCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<FrontCommandResult>.Success(replay with { Replay = true });
        }

        var existing = await _frontingRepository.GetActiveByFrontIdAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);
        FrontHistoryReadModel? existingHistory = null;
        if (existing is null)
        {
            existingHistory = await _frontingRepository.GetHistoryEntryByFrontIdAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);
            if (existingHistory is null)
                return RejectInvariant(command, "fronting:no_front");
        }

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType,
            command.PrincipalId,
            command.ExpectedVersion,
            cancellationToken);

        if (!versionAdvanced)
            return await RejectStaleVersion(command, cancellationToken);

        if (existing is not null)
        {
            var deleted = await _frontingRepository.EndByFrontIdAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);
            if (!deleted)
                return RejectInvariant(command, "fronting:delete_failed");
        }

        var deletedFromHistory = await _frontingRepository.DeleteFrontByIdAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);
        if (!deletedFromHistory)
            return RejectInvariant(command, "fronting:delete_failed");

        var alterId = existing?.Front.AlterId ?? existingHistory!.AlterId;
        var result = new FrontCommandResult(command.PrincipalId, alterId, command.Payload.FrontId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        if (existing is not null)
            await _eventBus.PublishAsync(new FrontingStateChangedEvent(command.PrincipalId), cancellationToken);
        await _eventBus.PublishAsync(new FrontDeletedEvent(command.PrincipalId, command.Payload.FrontId), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FrontCommandResult> RejectDuplicate(
        CommandEnvelope<DeleteFrontByIdCommand> command,
        string entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<FrontCommandResult> RejectInvariant(
        CommandEnvelope<DeleteFrontByIdCommand> command,
        string entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<FrontCommandResult>> RejectStaleVersion(
        CommandEnvelope<DeleteFrontByIdCommand> command,
        CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId, $"{AggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
    }
}

public sealed class UpdateFrontCommentCommandHandler : ICommandHandler<UpdateFrontCommentCommand, FrontCommandResult>
{
    private const string AggregateType = "fronting";

    private readonly IFrontingRepository _frontingRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public UpdateFrontCommentCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore,
        IClusterEventBus eventBus)
    {
        _frontingRepository = frontingRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<FrontCommandResult>> HandleAsync(
        CommandEnvelope<UpdateFrontCommentCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.FrontId))
            return RejectInvariant(command, "fronting:invalid_front_id");

        if ((command.Payload.Comment?.Length ?? 0) > 50)
            return RejectInvariant(command, "fronting:invalid_comment");

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "fronting:update_comment");

            var replay = CommandSerialization.Deserialize<FrontCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<FrontCommandResult>.Success(replay with { Replay = true });
        }

        var existing = await _frontingRepository.GetActiveByFrontIdAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);
        if (existing is null)
            return RejectInvariant(command, "fronting:no_front");

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType,
            command.PrincipalId,
            command.ExpectedVersion,
            cancellationToken);

        if (!versionAdvanced)
            return await RejectStaleVersion(command, cancellationToken);

        var updated = await _frontingRepository.UpdateCommentByFrontIdAsync(
            command.PrincipalId,
            command.Payload.FrontId,
            command.Payload.Comment ?? string.Empty,
            cancellationToken);

        if (!updated)
            return RejectInvariant(command, "fronting:update_comment_failed");

        var result = new FrontCommandResult(command.PrincipalId, existing.Front.AlterId, command.Payload.FrontId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        await _eventBus.PublishAsync(new FrontingStateChangedEvent(command.PrincipalId), cancellationToken);

        // Emit granular event for socket layer to handle front_updated
        await _eventBus.PublishAsync(new FrontCommentUpdatedEvent(command.PrincipalId, command.Payload.FrontId), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FrontCommandResult> RejectDuplicate(
        CommandEnvelope<UpdateFrontCommentCommand> command,
        string entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<FrontCommandResult> RejectInvariant(
        CommandEnvelope<UpdateFrontCommentCommand> command,
        string entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<FrontCommandResult>> RejectStaleVersion(
        CommandEnvelope<UpdateFrontCommentCommand> command,
        CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId, $"{AggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
    }
}
