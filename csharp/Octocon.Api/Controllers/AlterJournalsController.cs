using Microsoft.AspNetCore.Mvc;
using Octocon.Contracts.Operations;
using Octocon.Domain.Alters;
using Octocon.Domain.Journals;

namespace Octocon.Api.Controllers;

[Route("api/systems/me/alters")]
public sealed class AlterJournalsController : OctoconControllerBase
{
    private readonly IAlterRepository _alterRepository;
    private readonly IJournalRepository _journalRepository;
    private readonly CreateAlterJournalEntryCommandHandler _create;
    private readonly UpdateAlterJournalEntryCommandHandler _update;
    private readonly DeleteAlterJournalEntryCommandHandler _delete;
    private readonly SetAlterJournalLockedCommandHandler _setLocked;
    private readonly SetAlterJournalPinnedCommandHandler _setPinned;

    public AlterJournalsController(
        ApiSettings settings,
        IAlterRepository alterRepository,
        IJournalRepository journalRepository,
        CreateAlterJournalEntryCommandHandler create,
        UpdateAlterJournalEntryCommandHandler update,
        DeleteAlterJournalEntryCommandHandler delete,
        SetAlterJournalLockedCommandHandler setLocked,
        SetAlterJournalPinnedCommandHandler setPinned)
        : base(settings)
    {
        _alterRepository = alterRepository;
        _journalRepository = journalRepository;
        _create = create;
        _update = update;
        _delete = delete;
        _setLocked = setLocked;
        _setPinned = setPinned;
    }

    [HttpGet("{id:int}/journals")]
    public async Task<IActionResult> Index(int id, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var alterExists = await _alterRepository.ExistsAsync(principal, id, ct);
        if (!alterExists) return NotFound(new { code = "alter:not_found" });

        var entries = await _journalRepository.ListAlterAsync(principal, id, ct);
        return Ok(entries);
    }

    [HttpGet("journals/{journalId}")]
    public async Task<IActionResult> Show(string journalId, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var entry = await _journalRepository.GetAlterAsync(principal, journalId, ct);
        return entry is null ? NotFound(new { code = "journal:not_found" }) : Ok(entry);
    }

    [HttpPost("{id:int}/journals")]
    public async Task<IActionResult> Create(int id, [FromBody] CreateAlterJournalRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<CreateAlterJournalEntryCommand>(
            OperationIds.JournalAlterCreate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreateAlterJournalEntryCommand(id, req.Title)
        );

        return ToHttpResult(await _create.HandleAsync(envelope, ct));
    }

    [HttpPatch("journals/{journalId}")]
    public async Task<IActionResult> Update(string journalId, [FromBody] UpdateAlterJournalRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<UpdateAlterJournalEntryCommand>(
            OperationIds.JournalAlterUpdate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateAlterJournalEntryCommand(journalId, req.Title, req.Content, req.Color)
        );

        return ToHttpResult(await _update.HandleAsync(envelope, ct));
    }

    [HttpDelete("journals/{journalId}")]
    public async Task<IActionResult> Delete(string journalId, [FromBody] AlterJournalActionRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<DeleteAlterJournalEntryCommand>(
            OperationIds.JournalAlterDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAlterJournalEntryCommand(journalId)
        );

        return ToHttpResult(await _delete.HandleAsync(envelope, ct));
    }

    [HttpPost("journals/{journalId}/lock")]
    public async Task<IActionResult> Lock(string journalId, [FromBody] AlterJournalActionRequest? req, CancellationToken ct)
        => await SetLockedInternal(journalId, true, OperationIds.JournalAlterLock, req, ct);

    [HttpPost("journals/{journalId}/unlock")]
    public async Task<IActionResult> Unlock(string journalId, [FromBody] AlterJournalActionRequest? req, CancellationToken ct)
        => await SetLockedInternal(journalId, false, OperationIds.JournalAlterUnlock, req, ct);

    [HttpPost("journals/{journalId}/pin")]
    public async Task<IActionResult> Pin(string journalId, [FromBody] AlterJournalActionRequest? req, CancellationToken ct)
        => await SetPinnedInternal(journalId, true, OperationIds.JournalAlterPin, req, ct);

    [HttpPost("journals/{journalId}/unpin")]
    public async Task<IActionResult> Unpin(string journalId, [FromBody] AlterJournalActionRequest? req, CancellationToken ct)
        => await SetPinnedInternal(journalId, false, OperationIds.JournalAlterUnpin, req, ct);

    private async Task<IActionResult> SetLockedInternal(string journalId, bool locked, string operationId, AlterJournalActionRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<SetAlterJournalLockedCommand>(
            operationId,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetAlterJournalLockedCommand(journalId, locked)
        );

        return ToHttpResult(await _setLocked.HandleAsync(envelope, ct));
    }

    private async Task<IActionResult> SetPinnedInternal(string journalId, bool pinned, string operationId, AlterJournalActionRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<SetAlterJournalPinnedCommand>(
            operationId,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetAlterJournalPinnedCommand(journalId, pinned)
        );

        return ToHttpResult(await _setPinned.HandleAsync(envelope, ct));
    }
}

public sealed record CreateAlterJournalRequest(
    string Title,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record UpdateAlterJournalRequest(
    string? Title = null,
    string? Content = null,
    string? Color = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record AlterJournalActionRequest(
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);
