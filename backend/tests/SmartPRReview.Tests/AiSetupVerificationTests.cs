using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartPRReview.Api;
using SmartPRReview.Application.AI;
using SmartPRReview.Infrastructure.AI;
using Xunit;

namespace SmartPRReview.Tests;

public sealed class AiSetupVerificationTests
{
    [Fact]
    public async Task SavesVerifiedModelsWithoutKeyAndPreservesExistingSettings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SmartPRReview-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "appsettings.Ai.local.json");
            await File.WriteAllTextAsync(path, """{"ExecutionRunner":{"Url":"http://localhost:62700"},"AI":{"Providers":{"OpenAI":{"TimeoutSeconds":90}}}}""");
            var options = new AiOptions { Providers = new() { ["OpenAI"] = Config() } };
            using var services = new ServiceCollection()
                .AddSingleton<IOptions<AiOptions>>(Options.Create(options))
                .AddSingleton<IAiModelClient>(new ProbeClient()).BuildServiceProvider();
            Assert.Equal(0, await AiSetupVerification.RunAsync(services, "OpenAI", folder, default));
            var text = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("test-key", text);
            using var json = JsonDocument.Parse(text);
            var provider = json.RootElement.GetProperty("AI").GetProperty("Providers").GetProperty("OpenAI");
            Assert.True(provider.GetProperty("Enabled").GetBoolean());
            Assert.Equal(90, provider.GetProperty("TimeoutSeconds").GetInt32());
            Assert.All(provider.GetProperty("Models").EnumerateArray(), m => Assert.True(m.GetProperty("Verified").GetBoolean()));
            Assert.Equal("http://localhost:62700", json.RootElement.GetProperty("ExecutionRunner").GetProperty("Url").GetString());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static ProviderOptions Config() => new()
    {
        ApiKey = "test-key", ClassificationModel = "classifier", ReviewModel = "reviewer",
        Models = [AiTests.Model("classifier") with { TokenCounting = "Native" }, AiTests.Model("reviewer") with { TokenCounting = "Native" }]
    };

    [Fact]
    public async Task ValidatesClassificationToolRoundAndFinalUsingProductionLimits()
    {
        var client = new ProbeClient();
        await AiSetupVerification.VerifyAsync(client, Config(), new(), default);
        Assert.Equal(3, client.Requests.Count);
        Assert.Equal(1000, client.Requests[0].MaxOutputTokens);
        Assert.Equal(4000, client.Requests[1].MaxOutputTokens);
        Assert.Empty(client.Requests[2].Tools);
        Assert.NotNull(client.Requests[2].Previous);
        Assert.Single(client.Requests[2].ToolResults!);
    }

    [Fact]
    public async Task MissingKeyDoesNotMakeCalls()
    {
        var config = Config(); config.ApiKey = "";
        var client = new ProbeClient();
        await Assert.ThrowsAsync<AiRequestException>(() => AiSetupVerification.VerifyAsync(client, config, new(), default));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task DoesNotCertifyConservativeTokenizerWithOneSample()
    {
        var config = Config(); config.Models = config.Models.Select(m => m with { TokenCounting = "VerifiedUtf8UpperBound" }).ToArray();
        var client = new ProbeClient();
        await Assert.ThrowsAsync<AiRequestException>(() => AiSetupVerification.VerifyAsync(client, config, new(), default));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task MissingUsageCannotPassVerification()
    {
        var client = new ProbeClient { ReportUsage = false };
        await Assert.ThrowsAsync<AiRequestException>(() => AiSetupVerification.VerifyAsync(client, Config(), new(), default));
        Assert.Single(client.Requests);
    }

    private sealed class ProbeClient : IAiModelClient
    {
        public string Provider => "OpenAI";
        public bool ReportUsage { get; init; } = true;
        public List<AiRequest> Requests { get; } = [];
        public Task<int> CountInputAsync(AiRequest request, CancellationToken ct) => Task.FromResult(500);
        public Task<int?> CountTextAsync(string model, string text, CancellationToken ct) => Task.FromResult<int?>(50);
        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            var json = request.Model == "classifier"
                ? """{"category":"docs","rationale":"Documentación","scope":"README","evidence":["README.md"],"confidence":1}"""
                : """{"summary":"Prueba","findings":[],"limitations":["Tests omitidos"]}""";
            AiToolCall[] tools = request.Tools.Count > 0 ? [new("call1", "get_execution_result", JsonSerializer.SerializeToElement(new { }))] : [];
            return Task.FromResult(new AiResponse(tools.Length == 0 ? json : null, tools,
                ReportUsage ? 400 : null, null, 100, JsonSerializer.SerializeToElement(new object[0])));
        }
    }
}
