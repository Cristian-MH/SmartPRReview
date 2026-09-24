namespace SmartPRReview.Domain.Reviews;

public sealed record AiSelection(string Provider = "OpenAI", string? ClassificationModel = null, string? ReviewModel = null);
public sealed record ChangeClassification(string Category, string Rationale, string Scope, string[] Evidence, decimal Confidence);
public sealed record SkillIdentity(string Id, string Version, string Hash);
public sealed record ModelUsage(string Stage, string Provider, string Model, long? InputTokens, long? CachedInputTokens, long? OutputTokens, decimal? EstimatedCost, long DurationMs);
public sealed record ExecutionStep(string Name, string Status, int? ExitCode, string Log);
public sealed record ExecutionResult(string Status, string Reason, ExecutionStep[] Steps)
{
    public static ExecutionResult Skipped(string reason) => new("Skipped", reason, []);
}
public sealed record SpecialistReport(string Specialist, string Summary, ReviewFinding[] Findings, string[] Limitations);
public sealed class AiReview
{
    public AiSelection? Selection { get; set; }
    public ChangeClassification? Classification { get; set; }
    public List<string> Technologies { get; } = [];
    public List<SkillIdentity> Skills { get; } = [];
    public List<SpecialistReport> Specialists { get; } = [];
    public List<ModelUsage> Usage { get; } = [];
    public List<string> Limitations { get; } = [];
    public ExecutionResult? Execution { get; set; }
    public string Recommendation { get; set; } = "NeedsHumanReview";
    public string? BaseSha { get; set; }
    public string? HeadSha { get; set; }
}
