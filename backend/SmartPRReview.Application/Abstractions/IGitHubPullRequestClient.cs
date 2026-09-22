using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Application.Abstractions;

public interface IGitHubPullRequestClient
{
    Task<PullRequestSnapshot> GetAsync(
        string repositoryLocation,
        int pullRequestNumber,
        CancellationToken cancellationToken);
}

