using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SmartPRReview.Application.AI;
using SmartPRReview.Domain.Reviews;
using SmartPRReview.Infrastructure.AI;
using SmartPRReview.Infrastructure.Caching;
using Xunit;

namespace SmartPRReview.Tests;

public sealed class PipelineTests
{
    [Fact]
    public async Task IncompleteResponseKeepsUsageAndDoesNotRetryOrCompleteReview()
    {
        var client = new IncompleteClient();
        var result = await Pipeline(client, new Runner("Passed")).RunAsync(NewReview(), Selection(), null, null, default);
        Assert.Equal(ReviewStatus.Failed, result.Status);
        Assert.Equal("NeedsHumanReview", result.Ai!.Recommendation);
        var usage = Assert.Single(result.Ai.Usage);
        Assert.Equal(75, usage.InputTokens);
        Assert.Equal(1000, usage.OutputTokens);
        Assert.Equal(1, client.Calls);
    }
    private sealed class IncompleteClient : IAiModelClient
    {
        public string Provider => "OpenAI";
        public int Calls;
        public Task<int> CountInputAsync(AiRequest request, CancellationToken ct) => Task.FromResult(100);
        public Task<int?> CountTextAsync(string model, string text, CancellationToken ct) => Task.FromResult<int?>(null);
        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct)
        {
            Calls++;
            throw new AiRequestException("Límite de salida alcanzado.") { PartialResponse = new(null, [], 75, 0, 1000, default) };
        }
    }

    public static ReviewContext Context()
    {
        var snapshot = new PullRequestSnapshot(32, "Fix", "Description", "open", false, "user", "https://github.com/o/r/pull/32", "main", "branch",
            new string('a', 40), new string('b', 40), 1, 1, 1, 1, [new("a.cs", "modified", 1, 1, 2, null, "@@ -1 +1 @@\n-old\n+new")])
        { BaseRepository = "o/r", HeadRepository = "o/r" };
        var c = new ReviewContext(snapshot); c.Sources.Add(new("a.cs", "head", "new\nsecond line")); return c;
    }
    internal static ReviewPipeline Pipeline(IAiModelClient client, IExecutionRunner runner, ReviewLimits? limits = null, IPullRequestContextProvider? source = null)
    {
        var registry = new AiRegistry(AiTests.OptionsFor("OpenAI"), [client]);
        return new(registry, source ?? new Source(), new FileSystemSkillCatalog(), runner, new ToonInputFormatter(), limits ?? new(),
            new InMemoryReviewStore(new MemoryCache(new MemoryCacheOptions()), Options.Create(new ReviewCacheOptions())), TimeProvider.System);
    }
    internal static Review NewReview() => Review.Create(Guid.NewGuid(), new(RepositoryProvider.GitHub, "https://github.com/o/r", 32, null, null), DateTimeOffset.UtcNow);
    internal static ResolvedAi Selection() => new(new("OpenAI", "model", "model"), AiTests.Model(), AiTests.Model());

    [Fact]
    public async Task FullPipelineClassifiesAndPreservesEvidence()
    {
        var client = new ScriptedClient();
        var events = new List<string>();
        var result = await Pipeline(client, new Runner("Passed")).RunAsync(NewReview(), Selection(), "github-secret", (e, _) => { events.Add(e.Stage); return Task.CompletedTask; }, default);
        Assert.Equal(ReviewStatus.Completed, result.Status);
        Assert.Equal("fix", result.Ai!.Classification!.Category);
        Assert.Equal("Approve", result.Ai.Recommendation);
        Assert.Single(result.Ai.Specialists);
        Assert.Equal(2, result.Ai.Usage.Count);
        Assert.Contains("classification", events); Assert.Contains("execution", events);
        Assert.DoesNotContain("github-secret", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("github-secret", string.Join('\n', client.Inputs));
    }
    [Theory]
    [InlineData("Skipped", "NeedsHumanReview")][InlineData("Unavailable", "NeedsHumanReview")][InlineData("Failed", "RequestChanges")]
    public async Task ExecutionCoverageControlsRecommendation(string status, string recommendation)
    {
        var result = await Pipeline(new ScriptedClient(), new Runner(status)).RunAsync(NewReview(), Selection(), null, null, default);
        Assert.Equal(recommendation, result.Ai!.Recommendation);
    }
    [Fact]
    public async Task BudgetExhaustionRetainsClassificationButFailsReview()
    {
        var result = await Pipeline(new ScriptedClient(), new Runner("Passed"), new() { MaxCalls = 1 }).RunAsync(NewReview(), Selection(), null, null, default);
        Assert.Equal(ReviewStatus.Failed, result.Status);
        Assert.NotNull(result.Ai!.Classification);
        Assert.Equal("NeedsHumanReview", result.Ai.Recommendation);
        Assert.NotEmpty(result.Ai.Limitations);
    }
    [Fact]
    public async Task OneBoundedToolRoundContinuesSameProvider()
    {
        var client = new ScriptedClient { Tools = true };
        var result = await Pipeline(client, new Runner("Passed")).RunAsync(NewReview(), Selection(), null, null, default);
        Assert.Equal(ReviewStatus.Completed, result.Status);
        Assert.Equal(3, result.Ai!.Usage.Count);
        Assert.Contains("tool-result", client.Inputs);
    }
    [Theory]
    [InlineData("../secret")][InlineData("/etc/passwd")][InlineData("C:/secret")][InlineData("a\\b")]
    public async Task ToolsRejectEscapingPaths(string path)
    {
        var call = new AiToolCall("id", "read_file_range", JsonSerializer.SerializeToElement(new { path, revision = "head", startLine = 1, lineCount = 10 }));
        await Assert.ThrowsAsync<AiRequestException>(() => new ReviewContextTools(new Source()).ExecuteAsync(call, Context(), ExecutionResult.Skipped("test"), null, new ScriptedClient(), "model", default));
    }
    [Fact]
    public void SkillsRouteMixedTechnologiesAndTrackHashes()
    {
        var c = Context();
        var snapshot = c.Snapshot with { Files = [.. c.Snapshot.Files, new ChangedFileSnapshot("App.vue", "added", 1, 0, 1, null, "patch")] };
        var catalog = new FileSystemSkillCatalog();
        var selected = catalog.Select(new(snapshot));
        Assert.Contains(selected, s => s.Identity.Id == "dotnet"); Assert.Contains(selected, s => s.Identity.Id == "vue-typescript");
        Assert.All(selected, s => Assert.Equal(64, s.Identity.Hash.Length));
    }
    [Fact]
    public void FabricatedEvidenceIsNotAccepted()
    {
        Assert.False(ReviewPipeline.ValidFinding(new("bug", "bad", "other.cs", 1, "high", 1), Context()));
        Assert.False(ReviewPipeline.ValidFinding(new("bug", "bad", "a.cs", 999, "high", 1), Context()));
        Assert.True(ReviewPipeline.ValidFinding(new("bug", "bad", "a.cs", 1, "high", 1), Context()));
    }
    [Fact]
    public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipeline(new ScriptedClient(), new Runner("Passed")).RunAsync(NewReview(), Selection(), null, null, cts.Token));
    }
    internal sealed class Source : IPullRequestContextProvider
    {
        public Task<ReviewContext> CollectAsync(RepositoryReference repository, string? token, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Context()); }
        public Task<SourceFile?> ReadAsync(ReviewContext context, string path, string revision, string? token, CancellationToken ct) => Task.FromResult(context.Sources.FirstOrDefault(s => s.Path == path && s.Revision == revision));
        public Task<byte[]> ArchiveAsync(ReviewContext context, string? token, CancellationToken ct) => Task.FromResult(Array.Empty<byte>());
    }
    internal sealed class Runner(string status) : IExecutionRunner
    {
        public Task<ExecutionResult> ExecuteAsync(ReviewContext context, string? token, CancellationToken ct) => Task.FromResult(new ExecutionResult(status, status, []));
    }
    internal sealed class ScriptedClient : IAiModelClient
    {
        public bool Tools;
        public List<string> Inputs = [];
        public string Provider => "OpenAI";
        public Task<int> CountInputAsync(AiRequest request, CancellationToken ct) => Task.FromResult(100);
        public Task<int?> CountTextAsync(string model, string text, CancellationToken ct) => Task.FromResult<int?>(null);
        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct)
        {
            Inputs.Add(request.Input);
            if (request.ToolResults is not null) Inputs.Add("tool-result");
            var classification = request.Schema.GetProperty("properties").TryGetProperty("category", out _);
            var result = classification ? """{"category":"fix","rationale":"Corrige una condición","scope":"Validación","evidence":["a.cs"],"confidence":0.9}""" : """{"summary":"Revisión realizada","findings":[],"limitations":[]}""";
            AiToolCall[] calls = !classification && Tools && request.Previous is null ? [new("id", "read_file_range", JsonSerializer.SerializeToElement(new { path = "a.cs", revision = "head", startLine = 1, lineCount = 2 }))] : [];
            return Task.FromResult(new AiResponse(result, calls, 100, 0, 10, JsonSerializer.SerializeToElement(new { })));
        }
    }
}
