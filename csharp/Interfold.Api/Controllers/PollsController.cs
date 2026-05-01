using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Polls;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;

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
        var polls = await _pollRepository.ListAsync(PrincipalId, ct);
        return Ok(new { data = polls });
    }

    //TODO: To ensure route works as expected
    [HttpGet("{id}")]
    public async Task<IActionResult> Show(string id, CancellationToken ct)
    {
        var poll = await _pollRepository.GetAsync(PrincipalId, id, ct);
        return poll is null
            ? NotFound(new { error = "Poll not found.", code = "poll_not_found" })
            : Ok(new { data = poll });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePollRequest req, CancellationToken ct)
    {
        var principal = PrincipalId;
        var envelope = new CommandEnvelope<CreatePollCommand>(
            OperationIds.PollCreate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
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
        if (!req.TryResolveTimeEnd(out var resolvedTimeEnd))
        {
            return BadRequest(new { error = "Invalid time_end.", code = "poll_invalid_time_end" });
        }

        var envelope = new CommandEnvelope<UpdatePollCommand>(
            OperationIds.PollUpdate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdatePollCommand(id, req.Title, req.Description, resolvedTimeEnd, req.HasTimeEnd, req.Data)
        );

        var result = ToHttpResult(await _update.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromBody] BaseRequest? req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<DeletePollCommand>(
            OperationIds.PollDelete,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeletePollCommand(id)
        );

        var result = ToHttpResult(await _delete.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }
}
