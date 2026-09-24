namespace SmartPRReview.Domain.Reviews;

public sealed record Review(
    Guid Id,
    RepositoryReference Repository,
    ReviewStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Summary,
    PullRequestSnapshot? PullRequest,
    IReadOnlyCollection<ReviewFinding> Findings,
    string? Error)
{
    public AiReview? Ai { get; init; }
    public static Review Create(Guid id, RepositoryReference repository, DateTimeOffset now) =>
        new(id, repository, ReviewStatus.Processing, now, now, null, null, [], null);

    public Review Complete(
        string summary,
        PullRequestSnapshot? pullRequest,
        IReadOnlyCollection<ReviewFinding> findings,
        DateTimeOffset now) =>
        this with
        {
            Status = ReviewStatus.Completed,
            UpdatedAt = now,
            Summary = summary,
            PullRequest = pullRequest,
            Findings = findings,
            Error = null
        };

    public Review Fail(string error, DateTimeOffset now) => this with
    {
        Status = ReviewStatus.Failed,
        UpdatedAt = now,
        Error = error
    };
}
