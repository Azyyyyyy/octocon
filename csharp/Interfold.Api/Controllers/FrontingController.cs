using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Fronting;

namespace Interfold.Api.Controllers;

[Route("api/systems/me/front")]
public sealed class FrontingController : InterfoldControllerBase
{
    private readonly IFrontingRepository _repository;
    private readonly StartFrontCommandHandler _startHandler;
    private readonly EndFrontCommandHandler _endHandler;
    private readonly BulkUpdateFrontCommandHandler _bulkUpdateHandler;
    private readonly SetFrontCommandHandler _setHandler;
    private readonly SetPrimaryFrontCommandHandler _primaryHandler;
    private readonly DeleteFrontByIdCommandHandler _deleteByIdHandler;
    private readonly UpdateFrontCommentCommandHandler _updateCommentHandler;

    public FrontingController(
        IFrontingRepository repository,
        StartFrontCommandHandler startHandler,
        EndFrontCommandHandler endHandler,
        BulkUpdateFrontCommandHandler bulkUpdateHandler,
        SetFrontCommandHandler setHandler,
        SetPrimaryFrontCommandHandler primaryHandler,
        DeleteFrontByIdCommandHandler deleteByIdHandler,
        UpdateFrontCommentCommandHandler updateCommentHandler)
    {
        _repository = repository;
        _startHandler = startHandler;
        _endHandler = endHandler;
        _bulkUpdateHandler = bulkUpdateHandler;
        _setHandler = setHandler;
        _primaryHandler = primaryHandler;
        _deleteByIdHandler = deleteByIdHandler;
        _updateCommentHandler = updateCommentHandler;
    }

    //TODO: To ensure route works as expected
    [HttpPost]
    public async Task<IActionResult> Update([FromBody] FrontBulkUpdateRequest req, CancellationToken ct)
    {
        var payload = new BulkUpdateFrontCommand(
            req.Start.Select(x => new FrontStartItem(x.AlterId, x.Comment)).ToArray(),
            req.End.ToArray());

        var envelope = new CommandEnvelope<BulkUpdateFrontCommand>(
            OperationIds.FrontBulkUpdate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        var result = ToHttpResult(await _bulkUpdateHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] FrontStartRequest req, CancellationToken ct)
    {
        var alterId = req.ResolveAlterId();
        await CheckAlterId(alterId);

        var envelope = new CommandEnvelope<StartFrontCommand>(
            OperationIds.FrontStart, Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new StartFrontCommand(alterId, req.Comment)
        );

        var execution = await _startHandler.HandleAsync(envelope, ct);
        if (!execution.Accepted)
        {
            return ToHttpResult(execution);
        }

        if (!string.IsNullOrWhiteSpace(execution.Result?.FrontId))
        {
            Response.Headers.Location = $"/api/systems/me/front/{execution.Result.FrontId}";
        }

        return StatusCode(StatusCodes.Status201Created, new { frontId = execution.Result!.FrontId, replay = execution.Result.Replay });
    }

    [HttpPost("end")]
    public async Task<IActionResult> End([FromBody] FrontEndRequest req, CancellationToken ct)
    {
        var alterId = req.ResolveAlterId();
        await CheckAlterId(alterId);

        var envelope = new CommandEnvelope<EndFrontCommand>(
            OperationIds.FrontEnd, Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new EndFrontCommand(alterId)
        );
        var result = ToHttpResult(await _endHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("set")]
    public async Task<IActionResult> Set([FromBody] FrontSetRequest req, CancellationToken ct)
    {
        var alterId = req.ResolveAlterId();
        await CheckAlterId(alterId);

        var envelope = new CommandEnvelope<SetFrontCommand>(
            OperationIds.FrontSet,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetFrontCommand(alterId, req.Comment)
        );

        var result = ToHttpResult(await _setHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("primary")]
    public async Task<IActionResult> Primary([FromBody] FrontPrimaryRequest req, CancellationToken ct)
    {
        var alterId = req.ResolveAlterId();

        var envelope = new CommandEnvelope<SetPrimaryFrontCommand>(
            OperationIds.FrontPrimary, Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetPrimaryFrontCommand(alterId)
        );
        var result = ToHttpResult(await _primaryHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    //TODO: To ensure route works as expected
    [HttpGet("month")]
    public async Task<IActionResult> Month([FromQuery(Name = "end_anchor")] string endAnchor, CancellationToken ct)
    {
        if (!long.TryParse(endAnchor, out var unixEnd))
            return BadRequest(new { error = "Invalid end anchor. Please pass a valid Unix timestamp.", code = "invalid_end_anchor" });

        DateTimeOffset end;
        try
        {
            end = DateTimeOffset.FromUnixTimeSeconds(unixEnd);
        }
        catch
        {
            return BadRequest(new { error = "Invalid end anchor. Please pass a valid Unix timestamp.", code = "invalid_end_anchor" });
        }

        var start = end.AddDays(-30);
        var fronts = await _repository.ListHistoryBetweenAsync(PrincipalId, start, end, ct);
        return Ok(new { data = fronts });
    }

    [HttpGet("between")]
    public async Task<IActionResult> Between(
        [FromQuery(Name = "start")] string startAnchor,
        [FromQuery(Name = "end")] string endAnchor,
        CancellationToken ct)
    {
        if (!long.TryParse(startAnchor, out var unixStart) || !long.TryParse(endAnchor, out var unixEnd))
            return BadRequest(new { error = "Invalid start or end anchor. Please pass valid Unix timestamps.", code = "invalid_anchor" });

        DateTimeOffset start;
        DateTimeOffset end;
        try
        {
            start = DateTimeOffset.FromUnixTimeSeconds(unixStart);
            end = DateTimeOffset.FromUnixTimeSeconds(unixEnd);
        }
        catch
        {
            return BadRequest(new { error = "Invalid start or end anchor. Please pass valid Unix timestamps.", code = "invalid_anchor" });
        }

        var fronts = await _repository.ListHistoryBetweenAsync(PrincipalId, start, end, ct);
        return Ok(new { data = fronts });
    }

    //TODO: To ensure route works as expected
    [HttpGet("{id}")]
    public async Task<IActionResult> Show(string id, CancellationToken ct)
    {
        var front = await _repository.GetActiveByFrontIdAsync(PrincipalId, id, ct);
        return front is null
            ? NotFound(new { error = "Front not found.", code = "front_not_found" })
            : Ok(new { data = front });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromBody] BaseRequest? req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<DeleteFrontByIdCommand>(
            OperationIds.FrontDelete,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteFrontByIdCommand(id)
        );

        var result = ToHttpResult(await _deleteByIdHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("{id}/comment")]
    public async Task<IActionResult> UpdateComment(string id, [FromBody] FrontCommentRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UpdateFrontCommentCommand>(
            OperationIds.FrontCommentUpdate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateFrontCommentCommand(id, req.Comment)
        );

        var result = ToHttpResult(await _updateCommentHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }
}