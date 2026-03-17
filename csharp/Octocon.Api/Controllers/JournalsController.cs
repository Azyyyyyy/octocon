using Microsoft.AspNetCore.Mvc;
using Octocon.Contracts.Operations;
using Octocon.Domain.Journals;

namespace Octocon.Api.Controllers;

[Route("api/journals")]
public sealed class JournalsController : OctoconControllerBase
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
        ApiSettings settings,
        IJournalRepository journalRepository,
        CreateGlobalJournalEntryCommandHandler create,
        UpdateGlobalJournalEntryCommandHandler update,
        DeleteGlobalJournalEntryCommandHandler delete,
        SetGlobalJournalLockedCommandHandler setLocked,
        SetGlobalJournalPinnedCommandHandler setPinned,
        AttachAlterToGlobalJournalCommandHandler attachAlter,
        DetachAlterFromGlobalJournalCommandHandler detachAlter)
        : base(settings)
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
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var entries = await _journalRepository.ListGlobalAsync(principal, ct);
        return Ok(new { data = entries });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Show(string id, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var entry = await _journalRepository.GetGlobalAsync(principal, id, ct);
        return entry is null
            ? NotFound(new { error = "Journal entry not found.", code = "journal_entry_not_found" })
            : Ok(new { data = entry });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateGlobalJournalRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<CreateGlobalJournalEntryCommand>(
            OperationIds.JournalGlobalCreate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreateGlobalJournalEntryCommand(req.Title)
        );

        var execution = await _create.HandleAsync(envelope, ct);
        if (!execution.Accepted)
        {
            return ToHttpResult(execution);
        }

        var entry = await _journalRepository.GetGlobalAsync(principal, execution.Result!.EntryId, ct);
        object data = entry is not null ? entry : execution.Result;
        return StatusCode(StatusCodes.Status201Created, new { data, replay = execution.Result.Replay });
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateGlobalJournalRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<UpdateGlobalJournalEntryCommand>(
            OperationIds.JournalGlobalUpdate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateGlobalJournalEntryCommand(id, req.Title, req.Content, req.Color)
        );

        var result = ToHttpResult(await _update.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromBody] DeleteGlobalJournalRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<DeleteGlobalJournalEntryCommand>(
            OperationIds.JournalGlobalDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteGlobalJournalEntryCommand(id)
        );

        var result = ToHttpResult(await _delete.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("{id}/lock")]
    public async Task<IActionResult> Lock(string id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => await SetLockedInternal(id, true, OperationIds.JournalGlobalLock, req, ct);

    [HttpPost("{id}/unlock")]
    public async Task<IActionResult> Unlock(string id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => await SetLockedInternal(id, false, OperationIds.JournalGlobalUnlock, req, ct);

    [HttpPost("{id}/pin")]
    public async Task<IActionResult> Pin(string id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => await SetPinnedInternal(id, true, OperationIds.JournalGlobalPin, req, ct);

    [HttpPost("{id}/unpin")]
    public async Task<IActionResult> Unpin(string id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => await SetPinnedInternal(id, false, OperationIds.JournalGlobalUnpin, req, ct);

    [HttpPost("{id}/alter")]
    public async Task<IActionResult> AttachAlter(string id, [FromBody] JournalAlterRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<AttachAlterToGlobalJournalCommand>(
            OperationIds.JournalGlobalAttachAlter,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new AttachAlterToGlobalJournalCommand(id, req.AlterId)
        );

        var result = ToHttpResult(await _attachAlter.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpDelete("{id}/alter")]
    public async Task<IActionResult> DetachAlter(string id, [FromBody] JournalAlterRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<DetachAlterFromGlobalJournalCommand>(
            OperationIds.JournalGlobalDetachAlter,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DetachAlterFromGlobalJournalCommand(id, req.AlterId)
        );

        var result = ToHttpResult(await _detachAlter.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    private async Task<IActionResult> SetLockedInternal(string id, bool locked, string operationId, JournalActionRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<SetGlobalJournalLockedCommand>(
            operationId,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetGlobalJournalLockedCommand(id, locked)
        );

        var result = ToHttpResult(await _setLocked.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    private async Task<IActionResult> SetPinnedInternal(string id, bool pinned, string operationId, JournalActionRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<SetGlobalJournalPinnedCommand>(
            operationId,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetGlobalJournalPinnedCommand(id, pinned)
        );

        var result = ToHttpResult(await _setPinned.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }
}

public sealed record CreateGlobalJournalRequest(
    string Title,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record UpdateGlobalJournalRequest(
    string? Title = null,
    string? Content = null,
    string? Color = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record DeleteGlobalJournalRequest(
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record JournalActionRequest(
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record JournalAlterRequest(
    int AlterId,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);
