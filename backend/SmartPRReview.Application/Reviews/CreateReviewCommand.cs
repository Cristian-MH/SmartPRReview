using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Application.Reviews;

public sealed record CreateReviewCommand(RepositoryReference Repository, string? GitHubToken = null, AiSelection? Ai = null, string? AiApiKey = null)
{
    public override string ToString() => "CreateReviewCommand { credentials redacted }";
}
