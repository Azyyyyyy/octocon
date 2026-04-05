using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Interfold.Api.Controllers;

[Route("api")]
public sealed class HeartbeatController : InterfoldControllerBase
{
    public HeartbeatController() { }

    [AllowAnonymous]
    [HttpGet("heartbeat")]
    public IActionResult Heartbeat() => Ok(new { response = "ACK" });
}
