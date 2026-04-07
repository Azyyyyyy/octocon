using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using Interfold.Contracts.Operations;
using Interfold.Domain.Polls;
using System.Text.Json;

namespace Interfold.Api.Controllers;

[Route("api/polls")]
public sealed class PollsController : InterfoldControllerBase
{
    private readonly IPollRepository _pollRepository;
    private readonly CreatePollCommandHandler _create;
    private readonly UpdatePollCommandHandler _update;
    private readonly DeletePollCommandHandler _delete;

    public PollsController(
        IPollRepository pollRepository,
        CreatePollCommandHandler create,
        UpdatePollCommandHandler update,
        DeletePollCommandHandler delete)
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

    //TODO: To ensure route works as expected
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
            Payload: new CreatePollCommand(req.Title, req.Description, req.Type ?? "vote", req.TimeEnd)
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

        if (!req.TryResolveTimeEnd(out var resolvedTimeEnd))
        {
            return BadRequest(new { error = "Invalid time_end.", code = "poll_invalid_time_end" });
        }

        var envelope = new CommandEnvelope<UpdatePollCommand>(
            OperationIds.PollUpdate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdatePollCommand(id, req.Title, req.Description, resolvedTimeEnd, req.HasTimeEnd, req.Data)
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
    DateTime? TimeEnd = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record UpdatePollRequest(
    string? Title = null,
    string? Description = null,
    JsonElement TimeEnd = default,
    JsonElement? Data = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
)
{
    [JsonIgnore]
    public bool HasTimeEnd => TimeEnd.ValueKind != JsonValueKind.Undefined;

    public bool TryResolveTimeEnd(out DateTime? timeEnd)
    {
        timeEnd = null;

        if (!HasTimeEnd || TimeEnd.ValueKind == JsonValueKind.Null)
            return true;

        if (TimeEnd.ValueKind == JsonValueKind.String && TimeEnd.TryGetDateTime(out var parsed))
        {
            timeEnd = parsed;
            return true;
        }

        return false;
    }
}

public sealed record DeletePollRequest(
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);
