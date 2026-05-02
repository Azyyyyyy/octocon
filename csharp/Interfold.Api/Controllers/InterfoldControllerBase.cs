using System.Diagnostics;
using System.Net;
using Interfold.Api.Helpers;
using Interfold.Api.Middleware;
using Interfold.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Contracts;
using Interfold.Contracts.Models;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Api.Controllers;

[ApiController]
[Authorize]
public abstract class InterfoldControllerBase : ControllerBase
{
    protected string PrincipalId
    {
        get
        {
            if (HttpContext.Items.TryGetValue(InterfoldPrincipalMiddleware.PrincipalIdItemKey, out var value)
                && value is string principal)
            {
                return principal;
            }

            throw new InvalidOperationException(
                "PrincipalId is unavailable. Ensure InterfoldPrincipalMiddleware is configured.");
        }
    }

    protected async ValueTask CheckAlterId(int alterId, string? principal = null, CancellationToken? ct = null)
    {
        if (alterId <= 0)
        {
            throw new InterfoldException("Invalid alter ID.", "invalid_alter_id");
        }

        if (string.IsNullOrWhiteSpace(principal))
        {
            return;
        }

        if (!ct.HasValue)
        {
            throw new InterfoldException("CT is required on alter check", "alter_check_server_issue");
        }

        var alterRepository = this.HttpContext.RequestServices.GetRequiredService<IAlterRepository>();
        var alterExists = await alterRepository.ExistsAsync(principal, alterId, ct.Value);
        if (!alterExists)
        {
            throw new InterfoldException("Alter not found", "alter_not_found");
        }
    }

    protected string GetIdempotencyKey(string? bodyKey)
    {
        if (!string.IsNullOrWhiteSpace(bodyKey))
            return bodyKey;

        var header = Request.Headers["X-Interfold-Idempotency-Key"].FirstOrDefault();
        return !string.IsNullOrWhiteSpace(header) ? header : Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Returns <paramref name="url"/> with the server origin prepended when the stored
    /// value is a relative path. Already-absolute URLs are returned unchanged.
    /// </summary>
    protected string? QualifyUrl(string? url)
        => AvatarUrlQualifier.Qualify(url, Request.Scheme, Request.Host);

    /// <summary>
    /// Executes a command handler with:
    /// <list type="bullet">
    ///   <item>Latency measurement recorded in <see cref="InterfoldMetrics.CommandLatencyMs"/>.</item>
    ///   <item>Outcome counted in <see cref="InterfoldMetrics.CommandsTotal"/> (accepted / replay / rejected).</item>
    ///   <item>Conflict counted in <see cref="InterfoldMetrics.ConflictsTotal"/> when applicable.</item>
    ///   <item><c>X-Interfold-Command-Id</c> response header set from <paramref name="envelope"/>.</item>
    /// </list>
    /// </summary>
    protected async Task<IActionResult> ExecuteCommandAsync<TPayload, TResult>(
        ICommandHandler<TPayload, TResult> handler,
        CommandEnvelope<TPayload> envelope,
        CancellationToken ct)
        where TResult : ICommandResult
    {
        var sw = Stopwatch.StartNew();
        var result = await handler.HandleAsync(envelope, ct);
        sw.Stop();

        var opTag = new KeyValuePair<string, object?>("operation_id", envelope.OperationId);

        InterfoldMetrics.CommandLatencyMs.Record(
            sw.Elapsed.TotalMilliseconds,
            opTag);

        if (result.Accepted)
        {
            var outcome = result.Result!.Replay ? "replay" : "accepted";
            InterfoldMetrics.CommandsTotal.Add(1, opTag,
                new KeyValuePair<string, object?>("outcome", outcome));
        }
        else
        {
            InterfoldMetrics.CommandsTotal.Add(1, opTag,
                new KeyValuePair<string, object?>("outcome", "rejected"));

            InterfoldMetrics.ConflictsTotal.Add(1, opTag,
                new KeyValuePair<string, object?>("conflict_code",
                    result.Conflict!.Code.ToString()));
        }

        Response.Headers["X-Interfold-Command-Id"] = envelope.CommandId.ToString("N");

        if (result.Accepted)
            return Ok(result.Result);

        return result.Conflict!.Code switch
        {
            ConflictCode.ConflictDuplicate    => Conflict(result.Conflict),
            ConflictCode.ConflictInvariant    => UnprocessableEntity(result.Conflict),
            _                                 => StatusCode(500, new { Code = "unknown_error" })
        };
    }

    /// <summary>
    /// Maps a <see cref="CommandExecutionResult{T}"/> to a 204 No Content <see cref="Response{NoContent}"/>
    /// on success, or an <see cref="ErrorResponse"/> on failure.
    /// </summary>
    protected Response CommandNoContent<T>(CommandExecutionResult<T> result)
    {
        if (result.Accepted)
            return new Response();

        return ConflictToError(result.Conflict!);
    }

    /// <summary>
    /// Maps a <see cref="CommandExecutionResult{T}"/> to a 201 Created <see cref="Response{TData}"/>
    /// carrying the mapped <paramref name="dataSelector"/> result, or an <see cref="ErrorResponse"/> on failure.
    /// </summary>
    protected Response<TData> CommandCreated<T, TData>(CommandExecutionResult<T> result, Func<T, TData> dataSelector, Func<T?, bool?>? replaySelector = null)
    {
        if (result.Accepted)
            return new SuccessResponse<TData>(dataSelector(result.Result!), HttpStatusCode.Created, replaySelector?.Invoke(result.Result));

        return ConflictToError(result.Conflict!);
    }

    protected ErrorResponse ConflictToError(Interfold.Contracts.Operations.ConflictResult conflict)
    {
        this.Response.Headers["X-Interfold-OperationId"] = conflict.OperationId;
        
        return conflict.Code switch
        {
            ConflictCode.ConflictDuplicate => new ErrorResponse(
                "A duplicate conflict occurred.", conflict.ResolutionHint, HttpStatusCode.Conflict, conflict.EntityRef),
            ConflictCode.ConflictInvariant => new ErrorResponse(
                "The request could not be processed due to a conflict.", conflict.ResolutionHint,
                HttpStatusCode.UnprocessableEntity, conflict.EntityRef),
            _ => new ErrorResponse("An unknown error occurred.", "unknown_error", HttpStatusCode.InternalServerError, conflict.EntityRef)
        };
    }
}