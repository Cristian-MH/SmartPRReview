using Microsoft.AspNetCore.Mvc;

namespace SmartPRReview.Api.Controllers;

[ApiController]
[Route("health")]
[Tags("System")]
public sealed class HealthController : ControllerBase
{
    [HttpGet(Name = "HealthCheck")]
    public IActionResult Get() => Ok(new { status = "healthy" });
}
