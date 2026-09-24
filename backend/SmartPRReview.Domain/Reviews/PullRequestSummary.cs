namespace SmartPRReview.Domain.Reviews;

public sealed record PullRequestSummary(
    int Number,
    string Title,
    string? Description,
    string State,
    bool IsDraft,
    string Author,
    string WebUrl,
    string BaseReference,
    string HeadReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset? MergedAt);
