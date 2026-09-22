namespace SmartPRReview.Domain.Reviews;

public sealed record RepositoryReference(
    RepositoryProvider Provider,
    string Location,
    int? PullRequestNumber,
    string? BaseReference,
    string? HeadReference);

