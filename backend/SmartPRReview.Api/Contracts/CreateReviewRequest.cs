using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Api.Contracts;

public sealed record CreateReviewRequest(
    RepositoryProvider Provider,
    string Location,
    int? PullRequestNumber,
    string? BaseReference,
    string? HeadReference);
