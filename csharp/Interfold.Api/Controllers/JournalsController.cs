using Interfold.Api.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Journals;

namespace Interfold.Api.Controllers;

[Route("api/journals")]
public sealed class JournalsController : InterfoldControllerBase
{
    private readonly IJournalRepository _journalRepository;
    private readonly CreateGlobalJournalEntryCommandHandler _create;
    private readonly UpdateGlobalJournalEntryCommandHandler _update;
    private readonly DeleteGlobalJournalEntryCommandHandler _delete;
    private readonly SetGlobalJournalLockedCommandHandler _setLocked;
    private readonly SetGlobalJournalPinnedCommandHandler _setPinned;
    private readonly AttachAlterToGlobalJournalCommandHandler _attachAlter;
    private readonly DetachAlterFromGlobalJournalCommandHandler _detachAlter;

    public JournalsController(
        IJournalRepository journalRepository,
        CreateGlobalJournalEntryCommandHandler create,
        UpdateGlobalJournalEntryCommandHandler update,
        DeleteGlobalJournalEntryCommandHandler delete,
        SetGlobalJournalLockedCommandHandler setLocked,
        SetGlobalJournalPinnedCommandHandler setPinned,
        AttachAlterToGlobalJournalCommandHandler attachAlter,
        DetachAlterFromGlobalJournalCommandHandler detachAlter)
    {
        _journalRepository = journalRepository;
        _create = create;
        _update = update;
        _delete = delete;
        _setLocked = setLocked;
        _setPinned = setPinned;
        _attachAlter = attachAlter;
        _detachAlter = detachAlter;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var entries = await _journalRepository.ListGlobalAsync(PrincipalId, ct);
        return Ok(new { data = entries });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Show(string id, CancellationToken ct)
    {
        var entry = await _journalRepository.GetGlobalAsync(PrincipalId, id, ct);
        return entry is null
            ? NotFound(new { error = "Journal entry not found.", code = "journal_entry_not_found" })
            : Ok(new { data = entry });
    }

    [HttpPost]
    public async Task<Response<JournalReadModel>> Create([FromBody] CreateGlobalJournalRequest req, CancellationToken ct)
    {
        var principal = PrincipalId;
        var envelope = new CommandEnvelope<CreateGlobalJournalEntryCommand>(
            OperationIds.JournalGlobalCreate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreateGlobalJournalEntryCommand(req.Title)
        );

        var execution = await _create.HandleAsync(envelope, ct);
        if (!execution.Accepted)
        {
            return ConflictToError(execution.Conflict!);
        }

        var entry = await _journalRepository.GetGlobalAsync(principal, execution.Result!.EntryId, ct);
        if (entry is null)
            return new ErrorResponse("An unknown error occurred.", "unknown_error", System.Net.HttpStatusCode.InternalServerError);

        return new SuccessResponse<JournalReadModel>(entry, System.Net.HttpStatusCode.Created, execution.Result.Replay);
    }

    [HttpPatch("{id}")]
    public async Task<Response> Update(string id, [FromBody] UpdateGlobalJournalRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UpdateGlobalJournalEntryCommand>(
            OperationIds.JournalGlobalUpdate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateGlobalJournalEntryCommand(id, req.Title, req.Content, req.Color)
        );

        return CommandNoContent(await _update.HandleAsync(envelope, ct));
    }

    [HttpDelete("{id}")]
    public async Task<Response> Delete(string id, [FromBody] DeleteGlobalJournalRequest? req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<DeleteGlobalJournalEntryCommand>(
            OperationIds.JournalGlobalDelete,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteGlobalJournalEntryCommand(id)
        );

        return CommandNoContent(await _delete.HandleAsync(envelope, ct));
    }

    [HttpPost("{id}/lock")]
    public async Task<Response> Lock(string id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => await SetLockedInternal(id, true, OperationIds.JournalGlobalLock, req, ct);

    [HttpPost("{id}/unlock")]
    public async Task<Response> Unlock(string id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => await SetLockedInternal(id, false, OperationIds.JournalGlobalUnlock, req, ct);

    [HttpPost("{id}/pin")]
    public async Task<Response> Pin(string id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => await SetPinnedInternal(id, true, OperationIds.JournalGlobalPin, req, ct);

    [HttpPost("{id}/unpin")]
    public async Task<Response> Unpin(string id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => await SetPinnedInternal(id, false, OperationIds.JournalGlobalUnpin, req, ct);

    [HttpPost("{id}/alter")]
    public async Task<Response> AttachAlter(string id, [FromBody] JournalAlterRequest req, CancellationToken ct)
    {
        var alterId = req.AlterId ?? 0;
        await CheckAlterId(alterId);

        var envelope = new CommandEnvelope<AttachAlterToGlobalJournalCommand>(
            OperationIds.JournalGlobalAttachAlter,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new AttachAlterToGlobalJournalCommand(id, alterId)
        );

        return CommandNoContent(await _attachAlter.HandleAsync(envelope, ct));
    }

    [HttpDelete("{id}/alter")]
    public async Task<Response> DetachAlter(string id, [FromBody] JournalAlterRequest req, CancellationToken ct)
    {
        var alterId = req.AlterId ?? 0;
        await CheckAlterId(alterId);

        var envelope = new CommandEnvelope<DetachAlterFromGlobalJournalCommand>(
            OperationIds.JournalGlobalDetachAlter,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DetachAlterFromGlobalJournalCommand(id, alterId)
        );

        return CommandNoContent(await _detachAlter.HandleAsync(envelope, ct));
    }

    private async Task<Response> SetLockedInternal(string id, bool locked, string operationId, JournalActionRequest? req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<SetGlobalJournalLockedCommand>(
            operationId,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetGlobalJournalLockedCommand(id, locked)
        );

        return CommandNoContent(await _setLocked.HandleAsync(envelope, ct));
    }

    private async Task<Response> SetPinnedInternal(string id, bool pinned, string operationId, JournalActionRequest? req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<SetGlobalJournalPinnedCommand>(
            operationId,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetGlobalJournalPinnedCommand(id, pinned)
        );

        return CommandNoContent(await _setPinned.HandleAsync(envelope, ct));
    }
}