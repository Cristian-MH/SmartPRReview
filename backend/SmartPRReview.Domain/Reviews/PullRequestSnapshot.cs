namespace SmartPRReview.Domain.Reviews;

public sealed record PullRequestSnapshot(
    int Number,
    string Title,
    string? Description,
    string State,
    bool IsMerged,
    string Author,
    string WebUrl,
    string BaseReference,
    string HeadReference,
    string BaseSha,
    string HeadSha,
    int CommitCount,
    int Additions,
    int Deletions,
    int ChangedFileCount,
    IReadOnlyCollection<ChangedFileSnapshot> Files);

public sealed record ChangedFileSnapshot(
    string Path,
    string Status,
    int Additions,
    int Deletions,
    int Changes,
    string? PreviousPath,
    string? Patch);

