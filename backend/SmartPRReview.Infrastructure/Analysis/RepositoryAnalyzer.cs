using SmartPRReview.Application.Abstractions;
using SmartPRReview.Application.Reviews;
using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Infrastructure.Analysis;

public sealed class RepositoryAnalyzer(IGitHubPullRequestClient gitHubClient) : IRepositoryAnalyzer
{
    public async Task<AnalysisResult> AnalyzeAsync(
        RepositoryReference repository,
        CancellationToken cancellationToken)
    {
        if (repository.Provider is not RepositoryProvider.GitHub)
        {
            return new AnalysisResult(
                $"Provider '{repository.Provider}' is accepted but retrieval is not implemented yet.",
                null,
                []);
        }

        if (repository.PullRequestNumber is null)
        {
            throw new ReviewProcessingException(
                "A pull request number is required to retrieve information from GitHub.");
        }

        var pullRequest = await gitHubClient.GetAsync(
            repository.Location,
            repository.PullRequestNumber.Value,
            cancellationToken);

        var summary = $"Retrieved GitHub pull request #{pullRequest.Number}: " +
                      $"{pullRequest.Title}. {pullRequest.ChangedFileCount} changed files, " +
                      $"{pullRequest.Additions} additions and {pullRequest.Deletions} deletions.";

        return new AnalysisResult(summary, pullRequest, []);
    }
}
