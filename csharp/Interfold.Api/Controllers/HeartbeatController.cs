using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Interfold.Api.Controllers;

//TODO: To ensure route works as expected
[Route("api")]
public sealed class HeartbeatController : InterfoldControllerBase
{
    public HeartbeatController() { }

    [AllowAnonymous]
    [HttpGet("heartbeat")]
    public IActionResult Heartbeat() => Ok(new { response = "ACK" });
}
