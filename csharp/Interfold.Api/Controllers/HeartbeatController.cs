using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Interfold.Api.Controllers;

[AllowAnonymous]
[Route("api")]
public sealed class HeartbeatController : InterfoldControllerBase
{
    public HeartbeatController() { }

    [HttpGet("heartbeat")]
    public IActionResult Heartbeat() => Ok(new { response = "ACK" });
}
