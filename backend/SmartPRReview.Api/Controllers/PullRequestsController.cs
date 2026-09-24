using Microsoft.AspNetCore.Mvc;
using SmartPRReview.Api.Contracts;
using SmartPRReview.Application.Abstractions;
using SmartPRReview.Application.Reviews;
using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Api.Controllers;

[ApiController]
[Route("api/pull-requests")]
public sealed class PullRequestsController(IGitHubPullRequestClient client) : ControllerBase
{
    [HttpPost("list", Name = "ListPullRequests")]
    [ProducesResponseType(typeof(IReadOnlyCollection<PullRequestSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<IReadOnlyCollection<PullRequestSummary>>> List(
        ListPullRequestsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await client.ListAsync(
                request.Location, request.State, cancellationToken, request.GitHubToken));
        }
        catch (ArgumentException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: exception.Message);
        }
        catch (ReviewProcessingException exception)
        {
            return Problem(statusCode: StatusCodes.Status502BadGateway, detail: exception.Message);
        }
        catch (HttpRequestException)
        {
            return Problem(statusCode: StatusCodes.Status502BadGateway, detail: "GitHub could not complete the request.");
        }
    }
}
