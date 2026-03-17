using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Octocon.Api.Controllers;

[Route("api")]
public sealed class HeartbeatController : OctoconControllerBase
{
    public HeartbeatController(ApiSettings settings) : base(settings) { }

    [AllowAnonymous]
    [HttpGet("heartbeat")]
    public IActionResult Heartbeat() => Ok(new { response = "ACK" });
}
