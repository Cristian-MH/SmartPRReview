using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Application.Abstractions;

public interface IRepositoryAnalyzer
{
    Task<AnalysisResult> AnalyzeAsync(
        RepositoryReference repository,
        CancellationToken cancellationToken);
}

public sealed record AnalysisResult(
    string Summary,
    PullRequestSnapshot? PullRequest,
    IReadOnlyCollection<ReviewFinding> Findings);
