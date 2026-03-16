using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Octocon.Domain.Abstractions;

namespace Octocon.Api.Controllers;

/// <summary>
/// Exposes node-role metadata for load-balancer and ops health checks.
/// </summary>
[Route("health")]
public sealed class NodeRoleController(
    ApiSettings settings,
    INodeRoleContext nodeRole) : OctoconControllerBase(settings)
{
    /// <summary>
    /// Returns the current node's role and whether it owns singleton background tasks.
    /// <para>
    /// Equivalent to <c>Octocon.RPC.NodeTracker.current_group/0</c> from the legacy runtime.
    /// </para>
    /// </summary>
    [AllowAnonymous]
    [HttpGet("node-role")]
    public IActionResult GetNodeRole()
    {
        return Ok(new
        {
            role           = nodeRole.Role.ToString().ToLowerInvariant(),
            owns_singletons = nodeRole.IsPrimary
        });
    }
}
