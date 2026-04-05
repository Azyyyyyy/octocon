using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Interfold.Domain.Abstractions;
using Interfold.Infrastructure.Persistence.Bootstrap;

namespace Interfold.Api.Controllers;

/// <summary>
/// Exposes node-role metadata for load-balancer and ops health checks.
/// </summary>
[Route("health")]
public sealed class NodeRoleController : InterfoldControllerBase
{
    private readonly INodeRoleContext _nodeRole;
    private readonly IOperationalHealthChecker _healthChecker;

    public NodeRoleController(INodeRoleContext nodeRole, IOperationalHealthChecker healthChecker)
    {
        _nodeRole = nodeRole;
        _healthChecker = healthChecker;
    }

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
            role = _nodeRole.Role.ToString().ToLowerInvariant(),
            owns_singletons = _nodeRole.IsPrimary
        });
    }

    /// <summary>
    /// Returns the operational health status of the database and guarded paths.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("database")]
    public async Task<IActionResult> GetDatabaseHealth()
    {
        var result = await _healthChecker.CheckGuardedPathsAsync();
        return Ok(new
        {
            healthy = result.Healthy,
            paths = result.Paths.Select(p => new { p.Path, p.Healthy, p.Message }).ToList()
        });
    }
}
