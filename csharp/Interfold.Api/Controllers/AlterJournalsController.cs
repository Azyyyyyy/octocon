using System.Net;
using Interfold.Api.Models;
using Interfold.Contracts;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Journals;

namespace Interfold.Api.Controllers;

[Route("api/systems/me/alters")]
public sealed class AlterJournalsController : InterfoldControllerBase
{
    private readonly IJournalRepository _journalRepository;
    private readonly CreateAlterJournalEntryCommandHandler _create;
    private readonly UpdateAlterJournalEntryCommandHandler _update;
    private readonly DeleteAlterJournalEntryCommandHandler _delete;
    private readonly SetAlterJournalLockedCommandHandler _setLocked;
    private readonly SetAlterJournalPinnedCommandHandler _setPinned;

    public AlterJournalsController(
        IJournalRepository journalRepository,
        CreateAlterJournalEntryCommandHandler create,
        UpdateAlterJournalEntryCommandHandler update,
        DeleteAlterJournalEntryCommandHandler delete,
        SetAlterJournalLockedCommandHandler setLocked,
        SetAlterJournalPinnedCommandHandler setPinned)
    {
        _journalRepository = journalRepository;
        _create = create;
        _update = update;
        _delete = delete;
        _setLocked = setLocked;
        _setPinned = setPinned;
    }

    [HttpGet("{alterId:int}/journals")]
    public async Task<ActionResult<Response<IEnumerable<AlterJournalReadModel>>>> Index(int alterId, CancellationToken ct)
    {
        var principal = PrincipalId;
        await CheckAlterId(alterId, principal, ct);

        IEnumerable<AlterJournalReadModel> entries = await _journalRepository.ListAlterAsync(principal, alterId, ct) ?? [];
        return new Response<IEnumerable<AlterJournalReadModel>>(entries);
    }

    [HttpGet("journals/{journalId}")]
    public async Task<ActionResult<Response<AlterJournalReadModel>>> Show(string journalId, CancellationToken ct)
    {
        var entry = await _journalRepository.GetAlterAsync(PrincipalId, journalId, ct);
        return entry is null 
            ? throw new InterfoldException("Journal entry not found", "journal_entry_not_found", HttpStatusCode.NotFound) 
            : new Response<AlterJournalReadModel>(entry);
    }

    [HttpPost("{alterId:int}/journals")]
    public async Task<IActionResult> Create(int alterId, [FromBody] CreateAlterJournalRequest req, CancellationToken ct)
    {
        var principal = PrincipalId;
        await CheckAlterId(alterId);

        var envelope = new CommandEnvelope<CreateAlterJournalEntryCommand>(
            OperationIds.JournalAlterCreate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreateAlterJournalEntryCommand(alterId, req.Title)
        );

        var execution = await _create.HandleAsync(envelope, ct);
        if (!execution.Accepted)
        {
            return ToHttpResult(execution);
        }

        var entry = await _journalRepository.GetAlterAsync(principal, execution.Result!.EntryId, ct);
        object data = entry is not null ? entry : execution.Result;
        return StatusCode(StatusCodes.Status201Created, new { data, replay = execution.Result.Replay });
    }

    [HttpPatch("journals/{journalId}")]
    public async Task<IActionResult> Update(string journalId, [FromBody] UpdateAlterJournalRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UpdateAlterJournalEntryCommand>(
            OperationIds.JournalAlterUpdate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateAlterJournalEntryCommand(journalId, req.Title, req.Content, req.Color)
        );

        var result = ToHttpResult(await _update.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpDelete("journals/{journalId}")]
    public async Task<IActionResult> Delete(string journalId, [FromBody] AlterJournalActionRequest? req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<DeleteAlterJournalEntryCommand>(
            OperationIds.JournalAlterDelete,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAlterJournalEntryCommand(journalId)
        );

        var result = ToHttpResult(await _delete.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
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
        var envelope = new CommandEnvelope<SetAlterJournalLockedCommand>(
            operationId,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetAlterJournalLockedCommand(journalId, locked)
        );

        var result = ToHttpResult(await _setLocked.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    private async Task<IActionResult> SetPinnedInternal(string journalId, bool pinned, string operationId, AlterJournalActionRequest? req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<SetAlterJournalPinnedCommand>(
            operationId,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetAlterJournalPinnedCommand(journalId, pinned)
        );

        var result = ToHttpResult(await _setPinned.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
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
