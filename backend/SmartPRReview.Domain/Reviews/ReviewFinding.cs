namespace SmartPRReview.Domain.Reviews;

public sealed record ReviewFinding(
    string Category,
    string Message,
    string? FilePath,
    int? Line,
    string Severity,
    decimal Confidence);

