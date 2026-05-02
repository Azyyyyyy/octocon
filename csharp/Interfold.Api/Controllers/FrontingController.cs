using Microsoft.AspNetCore.Mvc;
using Interfold.Api.Models;
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
    public async Task<Response> Update([FromBody] FrontBulkUpdateRequest req, CancellationToken ct)
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

        return CommandNoContent(await _bulkUpdateHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("start")]
    public async Task<Response<FrontStartedResponse>> Start([FromBody] FrontStartRequest req, CancellationToken ct)
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
        var response = CommandCreated(execution, r => new FrontStartedResponse(r.FrontId!), r => r?.Replay);

        if (response.IsT0 && !string.IsNullOrWhiteSpace(response.AsT0.Data.FrontId))
        {
            Response.Headers.Location = $"/api/systems/me/front/{response.AsT0.Data.FrontId}";
        }

        return response;
    }

    [HttpPost("end")]
    public async Task<Response> End([FromBody] FrontEndRequest req, CancellationToken ct)
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

        return CommandNoContent(await _endHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("set")]
    public async Task<Response> Set([FromBody] FrontSetRequest req, CancellationToken ct)
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

        return CommandNoContent(await _setHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("primary")]
    public async Task<Response> Primary([FromBody] FrontPrimaryRequest req, CancellationToken ct)
    {
        var alterId = req.ResolveAlterId();

        var envelope = new CommandEnvelope<SetPrimaryFrontCommand>(
            OperationIds.FrontPrimary, Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetPrimaryFrontCommand(alterId)
        );

        return CommandNoContent(await _primaryHandler.HandleAsync(envelope, ct));
    }

    //TODO: To ensure route works as expected
    [HttpGet("month")]
    public async Task<Response<IReadOnlyList<FrontHistoryReadModel>>> Month(
        [FromQuery(Name = "end_anchor")] string endAnchor,
        CancellationToken ct)
    {
        if (!long.TryParse(endAnchor, out var unixEnd))
            return new ErrorResponse(
                "Invalid end anchor. Please pass a valid Unix timestamp.",
                "invalid_end_anchor",
                System.Net.HttpStatusCode.BadRequest);

        DateTimeOffset end;
        try
        {
            end = DateTimeOffset.FromUnixTimeSeconds(unixEnd);
        }
        catch
        {
            return new ErrorResponse(
                "Invalid end anchor. Please pass a valid Unix timestamp.",
                "invalid_end_anchor",
                System.Net.HttpStatusCode.BadRequest);
        }

        var start = end.AddDays(-30);
        var fronts = await _repository.ListHistoryBetweenAsync(PrincipalId, start, end, ct);
        return new SuccessResponse<IReadOnlyList<FrontHistoryReadModel>>(fronts);
    }

    [HttpGet("between")]
    public async Task<Response<IReadOnlyList<FrontHistoryReadModel>>> Between(
        [FromQuery(Name = "start")] string startAnchor,
        [FromQuery(Name = "end")] string endAnchor,
        CancellationToken ct)
    {
        if (!long.TryParse(startAnchor, out var unixStart) || !long.TryParse(endAnchor, out var unixEnd))
            return new ErrorResponse(
                "Invalid start or end anchor. Please pass valid Unix timestamps.",
                "invalid_anchor",
                System.Net.HttpStatusCode.BadRequest);

        DateTimeOffset start;
        DateTimeOffset end;
        try
        {
            start = DateTimeOffset.FromUnixTimeSeconds(unixStart);
            end = DateTimeOffset.FromUnixTimeSeconds(unixEnd);
        }
        catch
        {
            return new ErrorResponse(
                "Invalid start or end anchor. Please pass valid Unix timestamps.",
                "invalid_anchor",
                System.Net.HttpStatusCode.BadRequest);
        }

        var fronts = await _repository.ListHistoryBetweenAsync(PrincipalId, start, end, ct);
        return new SuccessResponse<IReadOnlyList<FrontHistoryReadModel>>(fronts);
    }

    //TODO: To ensure route works as expected
    [HttpGet("{id}")]
    public async Task<Response<FrontActiveReadModel>> Show(string id, CancellationToken ct)
    {
        var front = await _repository.GetActiveByFrontIdAsync(PrincipalId, id, ct);
        return front is null
            ? new ErrorResponse("Front not found.", "front_not_found", System.Net.HttpStatusCode.NotFound)
            : new SuccessResponse<FrontActiveReadModel>(front);
    }

    [HttpDelete("{id}")]
    public async Task<Response> Delete(string id, [FromBody] BaseRequest? req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<DeleteFrontByIdCommand>(
            OperationIds.FrontDelete,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteFrontByIdCommand(id)
        );

        return CommandNoContent(await _deleteByIdHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("{id}/comment")]
    public async Task<Response> UpdateComment(string id, [FromBody] FrontCommentRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UpdateFrontCommentCommand>(
            OperationIds.FrontCommentUpdate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateFrontCommentCommand(id, req.Comment)
        );

        return CommandNoContent(await _updateCommentHandler.HandleAsync(envelope, ct));
    }
}