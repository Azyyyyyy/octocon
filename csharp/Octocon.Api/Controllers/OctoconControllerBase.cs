using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Octocon.Contracts.Operations;
using Octocon.Domain.Abstractions;

namespace Octocon.Api.Controllers;

[ApiController]
[Authorize]
public abstract class OctoconControllerBase : ControllerBase
{
    private readonly ApiSettings _settings;

    protected OctoconControllerBase(ApiSettings settings)
    {
        _settings = settings;
    }

    protected string? GetPrincipalId()
    {
        if (_settings.DevPrincipalAllowed)
        {
            var devHeader = Request.Headers["X-Octocon-Dev-Principal"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(devHeader))
                return devHeader;
        }

        var sub = User.FindFirst("sub")?.Value
                  ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return string.IsNullOrWhiteSpace(sub) ? null : sub;
    }

    protected string GetIdempotencyKey(string? bodyKey)
    {
        if (!string.IsNullOrWhiteSpace(bodyKey))
            return bodyKey;

        var header = Request.Headers["X-Octocon-Idempotency-Key"].FirstOrDefault();
        return !string.IsNullOrWhiteSpace(header) ? header : Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Executes a command handler with:
    /// <list type="bullet">
    ///   <item>Latency measurement recorded in <see cref="OctoconMetrics.CommandLatencyMs"/>.</item>
    ///   <item>Outcome counted in <see cref="OctoconMetrics.CommandsTotal"/> (accepted / replay / rejected).</item>
    ///   <item>Conflict counted in <see cref="OctoconMetrics.ConflictsTotal"/> when applicable.</item>
    ///   <item><c>X-Octocon-Command-Id</c> response header set from <paramref name="envelope"/>.</item>
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

        OctoconMetrics.CommandLatencyMs.Record(
            sw.Elapsed.TotalMilliseconds,
            opTag);

        if (result.Accepted)
        {
            var outcome = result.Result!.Replay ? "replay" : "accepted";
            OctoconMetrics.CommandsTotal.Add(1, opTag,
                new KeyValuePair<string, object?>("outcome", outcome));
        }
        else
        {
            OctoconMetrics.CommandsTotal.Add(1, opTag,
                new KeyValuePair<string, object?>("outcome", "rejected"));

            OctoconMetrics.ConflictsTotal.Add(1, opTag,
                new KeyValuePair<string, object?>("conflict_code",
                    result.Conflict!.Code.ToString()));
        }

        Response.Headers["X-Octocon-Command-Id"] = envelope.CommandId.ToString("N");

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
