using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartPRReview.Application.AI;
using SmartPRReview.Domain.Reviews;
using SmartPRReview.Infrastructure.AI;
using Xunit;

namespace SmartPRReview.Tests;

public sealed class AiTests
{
    [Theory]
    [InlineData("gpt-5-mini", "minimal")]
    [InlineData("gpt-5-mini-2025-08-07", "minimal")]
    [InlineData("gpt-4.1", null)]
    public async Task ReasoningSettingsMatchModelWithoutIncreasingOutputBudget(string model, string? effort)
    {
        var options = OptionsFor("OpenAI");
        options.Value.Providers["OpenAI"].Models = [Model(model)];
        var handler = new CaptureHandler("""{"status":"completed","output":[]}""");
        using var http = new HttpClient(handler);
        await new OpenAiModelClient(http, options).CompleteAsync(Request() with { Model = model }, default);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(1000, payload.RootElement.GetProperty("max_output_tokens").GetInt32());
        if (effort is null) Assert.False(payload.RootElement.TryGetProperty("reasoning", out _));
        else Assert.Equal(effort, payload.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task IncompleteOpenAiResponsePreservesReasonAndBilledUsage()
    {
        using var http = new HttpClient(new CaptureHandler("""{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[],"usage":{"input_tokens":500,"input_tokens_details":{"cached_tokens":0},"output_tokens":1000}}"""));
        var error = await Assert.ThrowsAsync<AiRequestException>(() => new OpenAiModelClient(http, OptionsFor("OpenAI")).CompleteAsync(Request(), default));
        Assert.Contains("1000 tokens", error.Message);
        Assert.False(error.Transient);
        Assert.Equal(500, error.PartialResponse!.InputTokens);
        Assert.Equal(1000, error.PartialResponse.OutputTokens);
    }

    [Fact]
    public void RequestCredentialsEnableNativeCandidatesWithoutChangingServerOptions()
    {
        var options = OptionsFor("OpenAI");
        var config = options.Value.Providers["OpenAI"];
        config.Enabled = false; config.ApiKey = "";
        config.Models = [Model() with { Verified = false, TokenCounting = "Native" }];
        var credentials = new AiRequestCredentials();
        var registry = new AiRegistry(options, [new FakeClient()], credentials);
        Assert.Single(registry.Catalog().Providers);
        Assert.False(registry.Catalog().Providers[0].HasServerCredentials);
        Assert.Throws<AiConfigurationException>(() => registry.Resolve(null));
        credentials.Set("user-key");
        Assert.Equal("model", registry.Resolve(null).Selection.ReviewModel);
        Assert.False(config.Enabled);
        Assert.Empty(config.ApiKey);
        Assert.DoesNotContain("user-key", JsonSerializer.Serialize(registry.Catalog()));
        Assert.Throws<AiConfigurationException>(() => credentials.Set("key\r\ninjected-header"));
    }

    [Theory]
    [InlineData("OpenAI")][InlineData("Gemini")][InlineData("DeepSeek")]
    public async Task RequestKeyOverridesServerKeyWithoutLeakingIntoPayloadOrRetryingWithFallback(string provider)
    {
        var handler = new CaptureHandler("server error body", HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler);
        var credentials = new AiRequestCredentials(); credentials.Set("request-only-key");
        IAiModelClient client = provider switch
        {
            "OpenAI" => new OpenAiModelClient(http, OptionsFor(provider), credentials),
            "Gemini" => new GeminiModelClient(http, OptionsFor(provider), credentials),
            _ => new DeepSeekModelClient(http, OptionsFor(provider), credentials)
        };
        var error = await Assert.ThrowsAsync<AiRequestException>(() => client.CompleteAsync(Request(), default));
        Assert.Contains("request-only-key", handler.Auth!);
        Assert.DoesNotContain("request-only-key", handler.Body!);
        Assert.DoesNotContain("request-only-key", error.ToString());
        Assert.False(error.Transient);
        credentials.Set(null);
        await Assert.ThrowsAsync<AiRequestException>(() => client.CompleteAsync(Request(), default));
        Assert.Contains("secret-must-not-leak", handler.Auth!);
    }

    internal static ModelDefinition Model(string id = "model") => new(id, true, true, 100000, true, true, true, "VerifiedUtf8UpperBound");
    internal static IOptions<AiOptions> OptionsFor(string provider) => Options.Create(new AiOptions { Providers = new() { [provider] = new ProviderOptions { Enabled = true, ApiKey = "secret-must-not-leak", ClassificationModel = "model", ReviewModel = "model", Models = [Model()] } } });
    internal static AiRequest Request(bool tools = true) => new("model", "Instructions", "untrusted input", ReviewSchemas.Specialist, 1000, tools ? ReviewSchemas.Tools : []);

    [Fact]
    public void CatalogAndDefaultsNeverExposeKeys()
    {
        var client = new FakeClient();
        var registry = new AiRegistry(OptionsFor("OpenAI"), [client]);
        Assert.Equal("OpenAI", registry.Resolve(null).Selection.Provider);
        Assert.DoesNotContain("secret-must-not-leak", JsonSerializer.Serialize(registry.Catalog()));
        Assert.Equal(400, Assert.Throws<AiConfigurationException>(() => registry.Resolve(new("Unknown"))).StatusCode);
        Assert.Equal(400, Assert.Throws<AiConfigurationException>(() => registry.Resolve(new("OpenAI", "wrong"))).StatusCode);
        var disabled = new AiRegistry(Options.Create(new AiOptions()), [client]);
        Assert.Equal(503, Assert.Throws<AiConfigurationException>(() => disabled.Resolve(null)).StatusCode);
        Assert.Empty(disabled.Catalog().Providers);
    }
    [Fact]
    public void UnverifiedModelsAreNotEnabled()
    {
        var options = OptionsFor("OpenAI");
        options.Value.Providers["OpenAI"].Models = [Model() with { Verified = false }];
        var registry = new AiRegistry(options, [new FakeClient()]);
        Assert.Empty(registry.Catalog().Providers);
        Assert.Throws<AiConfigurationException>(() => registry.Resolve(null));
    }
    [Fact]
    public void BudgetReservationsAreAtomicAndRetryIsGlobal()
    {
        var budget = new ReviewBudget(new ReviewLimits { MaxCalls = 2, MaxInput = 20, MaxOutput = 20 });
        var accepted = 0;
        Parallel.For(0, 20, _ => { try { budget.Reserve(10, 10, 10, 100); Interlocked.Increment(ref accepted); } catch (AiRequestException) { } });
        Assert.Equal(2, accepted);
        Assert.True(budget.TryRetry()); Assert.False(budget.TryRetry());
        Assert.Throws<AiRequestException>(() => budget.Reserve(1, 1, 10, 100));
    }
    [Theory]
    [InlineData("feat")][InlineData("fix")][InlineData("perf")][InlineData("style")][InlineData("refactor")]
    [InlineData("docs")][InlineData("test")][InlineData("build")][InlineData("ci")][InlineData("chore")]
    public void ClassificationSchemaAcceptsOnlyTheTaxonomy(string category)
    {
        var json = JsonSerializer.SerializeToElement(new { category, rationale = "r", scope = "s", evidence = new[] { "a.cs" }, confidence = 0.9 });
        ReviewSchemas.Validate(json, ReviewSchemas.Classification);
        Assert.Throws<AiRequestException>(() => ReviewSchemas.Validate(JsonSerializer.SerializeToElement(new { category = "other" }), ReviewSchemas.Classification));
    }
    [Fact]
    public void ToonUniformStringSubsetPreservesDelimitersAndNulls()
    {
        var result = ToonEncoder.Encode([new() { ["path"] = "a,b\"c\n", ["note"] = null }]);
        Assert.StartsWith("rows[1]{path,note}:\n  ", result);
        Assert.Contains("\"a,b\\\"c\\n\"", result);
        Assert.EndsWith(",null", result);
        Assert.Equal("rows[0]:", ToonEncoder.Encode([]));
        Assert.Throws<ArgumentException>(() => ToonEncoder.Encode([new() { ["a"] = "a" }, new() { ["b"] = "b" }]));
    }
    [Fact]
    public async Task UnknownTokenizerUsesCompactJson()
    {
        Dictionary<string, string?>[] rows = [new() { ["path"] = "a.cs" }];
        Assert.Equal(JsonSerializer.Serialize(rows), await new ToonInputFormatter().FormatAsync(new FakeClient(), "model", rows, default));
    }
    [Theory]
    [InlineData("OpenAI")][InlineData("Gemini")][InlineData("DeepSeek")]
    public async Task ProviderContractsPreserveToolsAndOpaqueContinuation(string provider)
    {
        string reply = provider switch
        {
            "OpenAI" => """{"status":"completed","output":[{"type":"function_call","call_id":"c1","name":"search_snapshot","arguments":"{\"query\":\"test\"}"}],"usage":{"input_tokens":10,"output_tokens":2}}""",
            "Gemini" => """{"candidates":[{"finishReason":"STOP","content":{"role":"model","parts":[{"functionCall":{"name":"search_snapshot","args":{"query":"test"}},"thoughtSignature":"opaque"}]}}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":2,"thoughtsTokenCount":3}}""",
            _ => """{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"reasoning_content":"opaque","tool_calls":[{"id":"c1","type":"function","function":{"name":"search_snapshot","arguments":"{\"query\":\"test\"}"}}]}}],"usage":{"prompt_tokens":10,"completion_tokens":2}}"""
        };
        var handler = new CaptureHandler(reply);
        using var http = new HttpClient(handler);
        IAiModelClient client = provider switch
        {
            "OpenAI" => new OpenAiModelClient(http, OptionsFor(provider)),
            "Gemini" => new GeminiModelClient(http, OptionsFor(provider)),
            _ => new DeepSeekModelClient(http, OptionsFor(provider))
        };
        var response = await client.CompleteAsync(Request(), default);
        Assert.Single(response.ToolCalls);
        Assert.Equal("search_snapshot", response.ToolCalls[0].Name);
        Assert.Equal(10, response.InputTokens);
        Assert.Null(response.CachedInputTokens);
        Assert.Equal(provider == "Gemini" ? 5 : 2, response.OutputTokens);
        Assert.DoesNotContain("secret-must-not-leak", handler.Body!);
        Assert.Contains("secret-must-not-leak", handler.Auth!);
        var call = response.ToolCalls[0];
        await client.CompleteAsync(Request() with { Previous = response, ToolResults = [new(call.Id, call.Name, "result")], Tools = [] }, default);
        Assert.Contains("result", handler.Body!);
        if (provider != "OpenAI") Assert.Contains("opaque", handler.Body!);
    }
    [Theory]
    [InlineData("OpenAI", "{\"status\":\"incomplete\",\"output\":[]}")]
    [InlineData("Gemini", "{\"candidates\":[]}")]
    [InlineData("DeepSeek", "{\"choices\":[{\"finish_reason\":\"length\"}]}")]
    public async Task IncompleteResponsesAreRejected(string provider, string body)
    {
        using var http = new HttpClient(new CaptureHandler(body));
        IAiModelClient client = provider switch { "OpenAI" => new OpenAiModelClient(http, OptionsFor(provider)), "Gemini" => new GeminiModelClient(http, OptionsFor(provider)), _ => new DeepSeekModelClient(http, OptionsFor(provider)) };
        await Assert.ThrowsAsync<AiRequestException>(() => client.CompleteAsync(Request(), default));
    }
    [Theory]
    [InlineData(401, false)][InlineData(429, true)][InlineData(500, true)]
    public async Task ErrorsAreSanitized(int status, bool transient)
    {
        using var http = new HttpClient(new CaptureHandler("secret-must-not-leak", (HttpStatusCode)status));
        var client = new OpenAiModelClient(http, OptionsFor("OpenAI"));
        var error = await Assert.ThrowsAsync<AiRequestException>(() => client.CompleteAsync(Request(), default));
        Assert.Equal(transient, error.Transient); Assert.DoesNotContain("secret-must-not-leak", error.ToString());
    }
    internal sealed class CaptureHandler(string reply, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Body, Auth;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Body = await request.Content!.ReadAsStringAsync(ct);
            Auth = request.Headers.Authorization?.ToString() ?? string.Join(',', request.Headers.GetValues("x-goog-api-key"));
            return new(status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
    internal sealed class FakeClient : IAiModelClient
    {
        public string Provider => "OpenAI";
        public Task<int> CountInputAsync(AiRequest request, CancellationToken ct) => Task.FromResult(100);
        public Task<int?> CountTextAsync(string model, string text, CancellationToken ct) => Task.FromResult<int?>(null);
        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct) => throw new NotImplementedException();
    }
}
