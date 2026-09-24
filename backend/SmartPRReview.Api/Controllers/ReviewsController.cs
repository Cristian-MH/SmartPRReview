using Microsoft.AspNetCore.Mvc;
using SmartPRReview.Api.Contracts;
using SmartPRReview.Application.Reviews;
using SmartPRReview.Application.AI;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Api.Controllers;

[ApiController]
[Route("api/reviews")]
public sealed class ReviewsController(ReviewService service) : ControllerBase
{
    private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    [HttpPost(Name = "CreateReview")]
    [ProducesResponseType(typeof(Review), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Review>> Create(
        CreateReviewRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Location))
        {
            ModelState.AddModelError(nameof(request.Location), "Repository location is required.");
        }

        if (request.PullRequestNumber is <= 0)
        {
            ModelState.AddModelError(nameof(request.PullRequestNumber), "Pull request number must be greater than zero.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var repository = new RepositoryReference(
            request.Provider,
            request.Location.Trim(),
            request.PullRequestNumber,
            request.BaseReference,
            request.HeadReference);
        try
        {
            var review = await service.CreateAsync(
                new CreateReviewCommand(repository, request.GitHubToken, request.Ai, request.AiApiKey), cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = review.Id }, review);
        }
        catch (AiConfigurationException exception)
        { return Problem(statusCode: exception.StatusCode, detail: exception.Message); }
    }

    [HttpPost("stream")]
    [Produces("application/x-ndjson")]
    public async Task<IActionResult> Stream(CreateReviewRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Location)) ModelState.AddModelError(nameof(request.Location), "Repository location is required.");
        if (request.Provider != RepositoryProvider.GitHub || request.PullRequestNumber is null or <= 0)
            ModelState.AddModelError(nameof(request.PullRequestNumber), "A positive GitHub pull request number is required.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try { service.Validate(request.Ai, request.AiApiKey); }
        catch (AiConfigurationException exception) { return Problem(statusCode: exception.StatusCode, detail: exception.Message); }
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Accel-Buffering"] = "no";
        async Task Send(ReviewProgress progress, CancellationToken ct)
        {
            await Response.WriteAsync(JsonSerializer.Serialize(progress, StreamJson) + "\n", ct);
            await Response.Body.FlushAsync(ct);
        }
        var repository = new RepositoryReference(request.Provider, request.Location.Trim(), request.PullRequestNumber, request.BaseReference, request.HeadReference);
        var review = await service.CreateAsync(new(repository, request.GitHubToken, request.Ai, request.AiApiKey), cancellationToken, Send);
        await Send(new("result", review.Id, "finished", "Resultado final.", review), cancellationToken);
        return new EmptyResult();
    }

    [HttpGet("{id:guid}", Name = "GetReview")]
    [ProducesResponseType(typeof(Review), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Review>> Get(Guid id, CancellationToken cancellationToken)
    {
        var review = await service.GetAsync(id, cancellationToken);
        return review is null ? NotFound() : Ok(review);
    }
}
