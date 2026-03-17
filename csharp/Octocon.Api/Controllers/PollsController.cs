using Microsoft.AspNetCore.Mvc;
using Octocon.Contracts.Operations;
using Octocon.Domain.Polls;

namespace Octocon.Api.Controllers;

[Route("api/polls")]
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
        return Ok(new { data = polls });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Show(string id, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var poll = await _pollRepository.GetAsync(principal, id, ct);
        return poll is null
            ? NotFound(new { error = "Poll not found.", code = "poll_not_found" })
            : Ok(new { data = poll });
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

        var execution = await _create.HandleAsync(envelope, ct);
        if (!execution.Accepted)
        {
            return ToHttpResult(execution);
        }

        var poll = await _pollRepository.GetAsync(principal, execution.Result!.PollId, ct);
        object data = poll is not null ? poll : execution.Result;
        return StatusCode(StatusCodes.Status201Created, new { data, replay = execution.Result.Replay });
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

        var result = ToHttpResult(await _update.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
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

        var result = ToHttpResult(await _delete.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
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
