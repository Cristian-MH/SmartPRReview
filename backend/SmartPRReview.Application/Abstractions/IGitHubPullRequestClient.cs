using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Application.Abstractions;

public interface IGitHubPullRequestClient
{
    Task<IReadOnlyCollection<PullRequestSummary>> ListAsync(
        string repositoryLocation,
        string state,
        CancellationToken cancellationToken,
        string? gitHubToken = null);

    Task<PullRequestSnapshot> GetAsync(
        string repositoryLocation,
        int pullRequestNumber,
        CancellationToken cancellationToken,
        string? gitHubToken = null);
}
