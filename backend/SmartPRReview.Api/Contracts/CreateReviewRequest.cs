using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Api.Contracts;

public sealed record CreateReviewRequest(
    RepositoryProvider Provider,
    string Location,
    int? PullRequestNumber,
    string? BaseReference,
    string? HeadReference,
    string? GitHubToken = null,
    AiSelection? Ai = null,
    string? AiApiKey = null)
{
    public override string ToString() => "CreateReviewRequest { credentials redacted }";
}
