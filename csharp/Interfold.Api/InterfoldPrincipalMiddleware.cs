using Interfold.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using System.Security.Claims;

namespace Interfold.Api;

/// <summary>
/// Resolves and validates principal IDs for Interfold API controllers.
/// </summary>
public sealed class InterfoldPrincipalMiddleware(RequestDelegate next)
{
    internal const string PrincipalIdItemKey = "Interfold.PrincipalId";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!RequiresInterfoldPrincipal(context))
        {
            await next(context);
            return;
        }

        var principalId = ResolvePrincipalId(context.User);
        if (principalId is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Items[PrincipalIdItemKey] = principalId;
        await next(context);
    }

    private static bool RequiresInterfoldPrincipal(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null)
            return false;

        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return false;

        var actionDescriptor = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        return actionDescriptor is not null
               && typeof(InterfoldControllerBase).IsAssignableFrom(actionDescriptor.ControllerTypeInfo.AsType());
    }

    private static string? ResolvePrincipalId(ClaimsPrincipal user)
    {
        var sub = user.FindFirst("sub")?.Value
                  ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return string.IsNullOrWhiteSpace(sub) ? null : sub;
    }
}
