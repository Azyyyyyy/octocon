using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;

namespace Interfold.Api.Controllers;

[ApiController]
[Authorize]
public abstract class InterfoldControllerBase : ControllerBase
{
    protected string? GetPrincipalId()
    {
        var sub = User.FindFirst("sub")?.Value
                  ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return string.IsNullOrWhiteSpace(sub) ? null : sub;
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

        return ToHttpResult(result);
    }

    protected IActionResult ToHttpResult<T>(CommandExecutionResult<T> result)
    {
        if (result.Accepted)
            return Ok(result.Result);

        return result.Conflict!.Code switch
        {
            ConflictCode.ConflictStaleVersion => Conflict(result.Conflict),
            ConflictCode.ConflictDuplicate    => Conflict(result.Conflict),
            ConflictCode.ConflictInvariant    => UnprocessableEntity(result.Conflict),
            _                                 => StatusCode(500, new { Code = "unknown_error" })
        };
    }
}
