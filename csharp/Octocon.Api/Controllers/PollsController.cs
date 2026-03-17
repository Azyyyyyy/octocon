using Microsoft.AspNetCore.Mvc;
using Octocon.Contracts.Operations;
using Octocon.Domain.Polls;

namespace Octocon.Api.Controllers;

[Route("api/systems/me/polls")]
public sealed class PollsController : OctoconControllerBase
{
    private readonly IPollRepository _pollRepository;
    private readonly CreatePollCommandHandler _create;
    private readonly UpdatePollCommandHandler _update;
    private readonly DeletePollCommandHandler _delete;

    public PollsController(
        ApiSettings settings,
        IPollRepository pollRepository,
        CreatePollCommandHandler create,
        UpdatePollCommandHandler update,
        DeletePollCommandHandler delete) : base(settings)
    {
        _pollRepository = pollRepository;
        _create = create;
        _update = update;
        _delete = delete;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var polls = await _pollRepository.ListAsync(principal, ct);
        return Ok(polls);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Show(string id, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var poll = await _pollRepository.GetAsync(principal, id, ct);
        return poll is null ? NotFound(new { code = "poll:not_found" }) : Ok(poll);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePollRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<CreatePollCommand>(
            OperationIds.PollCreate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreatePollCommand(req.Title, req.Description, req.Type ?? "vote", req.TimeEndIso)
        );

        return ToHttpResult(await _create.HandleAsync(envelope, ct));
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdatePollRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<UpdatePollCommand>(
            OperationIds.PollUpdate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdatePollCommand(id, req.Title, req.Description, req.TimeEndIso, req.DataJson)
        );

        return ToHttpResult(await _update.HandleAsync(envelope, ct));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromBody] DeletePollRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<DeletePollCommand>(
            OperationIds.PollDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeletePollCommand(id)
        );

        return ToHttpResult(await _delete.HandleAsync(envelope, ct));
    }
}

public sealed record CreatePollRequest(
    string Title,
    string? Description = null,
    string? Type = null,
    string? TimeEndIso = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record UpdatePollRequest(
    string? Title = null,
    string? Description = null,
    string? TimeEndIso = null,
    string? DataJson = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record DeletePollRequest(
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);
