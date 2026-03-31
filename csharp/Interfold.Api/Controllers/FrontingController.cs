using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using Interfold.Contracts.Operations;
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
        ApiSettings settings,
        IFrontingRepository repository,
        StartFrontCommandHandler startHandler,
        EndFrontCommandHandler endHandler,
        BulkUpdateFrontCommandHandler bulkUpdateHandler,
        SetFrontCommandHandler setHandler,
        SetPrimaryFrontCommandHandler primaryHandler,
        DeleteFrontByIdCommandHandler deleteByIdHandler,
        UpdateFrontCommentCommandHandler updateCommentHandler)
        : base(settings)
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
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var payload = new BulkUpdateFrontCommand(
            req.Start.Select(x => new FrontStartItem(x.AlterId, x.Comment)).ToArray(),
            req.End.ToArray());

        var envelope = new CommandEnvelope<BulkUpdateFrontCommand>(
            OperationIds.FrontBulkUpdate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        var result = ToHttpResult(await _bulkUpdateHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] FrontStartRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var alterId = req.ResolveAlterId();
        if (alterId <= 0)
            return BadRequest(new { error = "Invalid alter ID.", code = "invalid_alter_id" });

        var envelope = new CommandEnvelope<StartFrontCommand>(
            OperationIds.FrontStart, Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
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
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var alterId = req.ResolveAlterId();
        if (alterId <= 0)
            return BadRequest(new { error = "Invalid alter ID.", code = "invalid_alter_id" });

        var envelope = new CommandEnvelope<EndFrontCommand>(
            OperationIds.FrontEnd, Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new EndFrontCommand(alterId)
        );
        var result = ToHttpResult(await _endHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("set")]
    public async Task<IActionResult> Set([FromBody] FrontSetRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var alterId = req.ResolveAlterId();
        if (alterId <= 0)
            return BadRequest(new { error = "Invalid alter ID.", code = "invalid_alter_id" });

        var envelope = new CommandEnvelope<SetFrontCommand>(
            OperationIds.FrontSet,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetFrontCommand(alterId, req.Comment)
        );

        var result = ToHttpResult(await _setHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("primary")]
    public async Task<IActionResult> Primary([FromBody] FrontPrimaryRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var alterId = req.ResolveAlterId();

        var envelope = new CommandEnvelope<SetPrimaryFrontCommand>(
            OperationIds.FrontPrimary, Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
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
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

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
        var fronts = await _repository.ListHistoryBetweenAsync(principal, start, end, ct);
        return Ok(new { data = fronts });
    }

    [HttpGet("between")]
    public async Task<IActionResult> Between(
        [FromQuery(Name = "start")] string startAnchor,
        [FromQuery(Name = "end")] string endAnchor,
        CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

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

        var fronts = await _repository.ListHistoryBetweenAsync(principal, start, end, ct);
        return Ok(new { data = fronts });
    }

    //TODO: To ensure route works as expected
    [HttpGet("{id}")]
    public async Task<IActionResult> Show(string id, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var front = await _repository.GetActiveByFrontIdAsync(principal, id, ct);
        return front is null
            ? NotFound(new { error = "Front not found.", code = "front_not_found" })
            : Ok(new { data = front });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromBody] FrontCommandRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<DeleteFrontByIdCommand>(
            OperationIds.FrontDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteFrontByIdCommand(id)
        );

        var result = ToHttpResult(await _deleteByIdHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("{id}/comment")]
    public async Task<IActionResult> UpdateComment(string id, [FromBody] FrontCommentRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<UpdateFrontCommentCommand>(
            OperationIds.FrontCommentUpdate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateFrontCommentCommand(id, req.Comment)
        );

        var result = ToHttpResult(await _updateCommentHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }
}

public sealed record FrontBulkUpdateRequest(
    IReadOnlyList<FrontStartEntry> Start,
    IReadOnlyList<int> End,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record FrontStartEntry(int AlterId, string? Comment = null);

public sealed record FrontStartRequest(
    int? AlterId,
    [property: JsonPropertyName("id")] int? Id = null,
    string? Comment = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
)
{
    public int ResolveAlterId() => AlterId ?? Id ?? 0;
}

public sealed record FrontEndRequest(
    int? AlterId,
    [property: JsonPropertyName("id")] int? Id = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
)
{
    public int ResolveAlterId() => AlterId ?? Id ?? 0;
}

public sealed record FrontPrimaryRequest(
    int? AlterId = null,
    [property: JsonPropertyName("id")] int? Id = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
)
{
    public int? ResolveAlterId() => AlterId ?? Id;
}

public sealed record FrontSetRequest(
    int? AlterId,
    [property: JsonPropertyName("id")] int? Id = null,
    string? Comment = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
)
{
    public int ResolveAlterId() => AlterId ?? Id ?? 0;
}

public sealed record FrontCommentRequest(
    string Comment,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record FrontCommandRequest(
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);
