using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartPRReview.Application.AI;
using SmartPRReview.Infrastructure.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace SmartPRReview.Tests;

public sealed class ApiTests
{
    [Theory]
    [InlineData("/api/reviews")][InlineData("/api/reviews/stream")]
    public async Task RequestKeyIsScopedAndNeverReturned(string endpoint)
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<string?>();
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            var options = AiTests.OptionsFor("OpenAI").Value;
            options.Providers["OpenAI"].Enabled = false;
            options.Providers["OpenAI"].ApiKey = "";
            services.AddSingleton<IOptions<AiOptions>>(Options.Create(options));
            services.RemoveAll<IAiModelClient>();
            services.AddScoped<IAiModelClient>(p => new CredentialClient(p.GetRequiredService<AiRequestCredentials>(), seen));
            services.RemoveAll<IPullRequestContextProvider>(); services.AddSingleton<IPullRequestContextProvider, PipelineTests.Source>();
            services.RemoveAll<IExecutionRunner>(); services.AddSingleton<IExecutionRunner>(new PipelineTests.Runner("Passed"));
        }));
        using var client = app.CreateClient();
        await Task.WhenAll(new[] { "user-one-key", "user-two-key" }.Select(async key =>
        {
            using var response = await client.PostAsJsonAsync(endpoint, new { provider = "GitHub", location = "https://github.com/o/r", pullRequestNumber = 32, aiApiKey = key });
            Assert.True(response.IsSuccessStatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(key, body);
            if (response.Headers.Location is { } location)
                Assert.DoesNotContain(key, await client.GetStringAsync(location));
        }));
        Assert.Contains("user-one-key", seen);
        Assert.Contains("user-two-key", seen);
        Assert.DoesNotContain(null, seen);
        using var missing = await client.PostAsJsonAsync(endpoint, new { provider = "GitHub", location = "https://github.com/o/r", pullRequestNumber = 32 });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    private sealed class CredentialClient(AiRequestCredentials credentials, System.Collections.Concurrent.ConcurrentBag<string?> seen) : IAiModelClient
    {
        private readonly PipelineTests.ScriptedClient inner = new();
        private string? firstKey;
        public string Provider => "OpenAI";
        public Task<int> CountInputAsync(AiRequest request, CancellationToken ct) => inner.CountInputAsync(request, ct);
        public Task<int?> CountTextAsync(string model, string text, CancellationToken ct) => inner.CountTextAsync(model, text, ct);
        public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct)
        {
            firstKey ??= credentials.Get();
            await Task.Delay(10, ct);
            Assert.Equal(firstKey, credentials.Get());
            seen.Add(credentials.Get());
            return await inner.CompleteAsync(request, ct);
        }
    }

    [Fact]
    public async Task SelectionPreflightAndStreamingAreConsistent()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAiRegistry>();
            services.AddSingleton<IAiRegistry>(new AiRegistry(AiTests.OptionsFor("OpenAI"), [new PipelineTests.ScriptedClient()]));
            services.RemoveAll<IPullRequestContextProvider>(); services.AddSingleton<IPullRequestContextProvider, PipelineTests.Source>();
            services.RemoveAll<IExecutionRunner>(); services.AddSingleton<IExecutionRunner>(new PipelineTests.Runner("Passed"));
        }));
        using var client = app.CreateClient();
        var catalog = await client.GetStringAsync("/api/ai/models");
        Assert.Contains("OpenAI", catalog); Assert.DoesNotContain("secret-must-not-leak", catalog);
        var invalid = await client.PostAsJsonAsync("/api/reviews/stream", new { provider = "GitHub", location = "https://github.com/o/r", pullRequestNumber = 32, ai = new { provider = "Bad" } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var request = new { provider = "GitHub", location = "https://github.com/o/r", pullRequestNumber = 32 };
        var streamed = await client.PostAsJsonAsync("/api/reviews/stream", request);
        Assert.Equal(HttpStatusCode.OK, streamed.StatusCode);
        Assert.Equal("application/x-ndjson", streamed.Content.Headers.ContentType!.MediaType);
        var lines = (await streamed.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("\"type\":\"started\"", lines[0]); Assert.Contains("\"type\":\"result\"", lines[^1]); Assert.Contains("\"category\":\"fix\"", lines[^1]);
        var ordinary = await client.PostAsJsonAsync("/api/reviews", request);
        Assert.Equal(HttpStatusCode.Created, ordinary.StatusCode);
        Assert.NotNull(ordinary.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ordinary.Headers.Location)).StatusCode);
    }
}
