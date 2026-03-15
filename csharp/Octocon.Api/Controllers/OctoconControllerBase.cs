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
