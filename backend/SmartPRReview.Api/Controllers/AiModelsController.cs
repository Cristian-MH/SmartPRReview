using Microsoft.AspNetCore.Mvc;
using SmartPRReview.Application.AI;

namespace SmartPRReview.Api.Controllers;

[ApiController]
[Route("api/ai/models")]
public sealed class AiModelsController(IAiRegistry registry) : ControllerBase
{
    [HttpGet]
    public ActionResult<AiCatalog> Get() => Ok(registry.Catalog());
}
