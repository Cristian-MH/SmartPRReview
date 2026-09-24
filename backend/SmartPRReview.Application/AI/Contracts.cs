using System.Text.Json;
using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Application.AI;

public sealed class AiConfigurationException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
public sealed class AiRequestException(string message, bool transient = false) : Exception(message)
{
    public bool Transient { get; } = transient;
    public AiResponse? PartialResponse { get; init; }
}
public sealed record ModelDefinition(string Id, bool Classification, bool Review, int ContextTokens,
    bool Tools, bool StructuredOutput, bool Verified, string TokenCounting,
    decimal? InputRate = null, decimal? CachedInputRate = null, decimal? OutputRate = null, string? ReasoningEffort = null);
public sealed record ProviderCatalog(string Provider, string ClassificationModel, string ReviewModel, ModelDefinition[] Models, bool HasServerCredentials = false);
public sealed record AiCatalog(string DefaultProvider, ProviderCatalog[] Providers);
public sealed record ResolvedAi(AiSelection Selection, ModelDefinition Classifier, ModelDefinition Reviewer);
public sealed record AiTool(string Name, string Description, JsonElement Parameters);
public sealed record AiToolCall(string Id, string Name, JsonElement Arguments);
public sealed record AiToolResult(string Id, string Name, string Output);
public sealed record AiRequest(string Model, string Instructions, string Input, JsonElement Schema,
    int MaxOutputTokens, IReadOnlyList<AiTool> Tools, AiResponse? Previous = null,
    IReadOnlyList<AiToolResult>? ToolResults = null);
public sealed record AiResponse(string? Json, AiToolCall[] ToolCalls, long? InputTokens, long? CachedInputTokens,
    long? OutputTokens, JsonElement Continuation);
public interface IAiModelClient
{
    string Provider { get; }
    Task<int> CountInputAsync(AiRequest request, CancellationToken cancellationToken);
    Task<int?> CountTextAsync(string model, string text, CancellationToken cancellationToken);
    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken);
}
public interface IAiRegistry
{
    AiCatalog Catalog();
    ResolvedAi Resolve(AiSelection? selection);
    IAiModelClient Client(string provider);
}
public sealed record SourceFile(string Path, string Revision, string Content);
public sealed class ReviewContext(PullRequestSnapshot snapshot)
{
    public PullRequestSnapshot Snapshot { get; } = snapshot;
    public List<SourceFile> Sources { get; } = [];
    public List<string> Commits { get; } = [];
    public List<string> Limitations { get; } = [];
    public int Bytes { get; set; }
}
public interface IPullRequestContextProvider
{
    Task<ReviewContext> CollectAsync(RepositoryReference repository, string? token, CancellationToken cancellationToken);
    Task<SourceFile?> ReadAsync(ReviewContext context, string path, string revision, string? token, CancellationToken cancellationToken);
    Task<byte[]> ArchiveAsync(ReviewContext context, string? token, CancellationToken cancellationToken);
}
public sealed record Skill(SkillIdentity Identity, string Instructions, string[] Extensions, string[] Markers, string[] References);
public interface ISkillCatalog
{
    Skill Get(string id);
    IReadOnlyList<Skill> Select(ReviewContext context);
}
public interface IExecutionRunner
{
    Task<ExecutionResult> ExecuteAsync(ReviewContext context, string? token, CancellationToken cancellationToken);
}
public interface IModelInputFormatter
{
    Task<string> FormatAsync(IAiModelClient client, string model, IReadOnlyList<Dictionary<string, string?>> rows, CancellationToken cancellationToken);
}
public sealed record ReviewProgress(string Type, Guid ReviewId, string Stage, string Message, Review? Review = null);
public delegate Task ProgressSink(ReviewProgress progress, CancellationToken cancellationToken);
public sealed class ReviewLimits
{
    public int ClassificationInput { get; set; } = 12000;
    public int ClassificationOutput { get; set; } = 1000;
    public int SpecialistInput { get; set; } = 16000;
    public int SpecialistOutput { get; set; } = 4000;
    public int MaxCalls { get; set; } = 10;
    public int MaxInput { get; set; } = 80000;
    public int MaxOutput { get; set; } = 18000;
    public int MaxSpecialists { get; set; } = 4;
}
