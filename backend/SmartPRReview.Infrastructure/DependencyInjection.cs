using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartPRReview.Application.Abstractions;
using SmartPRReview.Infrastructure.Analysis;
using SmartPRReview.Infrastructure.Caching;
using SmartPRReview.Infrastructure.GitHub;
using SmartPRReview.Application.AI;
using SmartPRReview.Infrastructure.AI;
using SmartPRReview.Infrastructure.Execution;

namespace SmartPRReview.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddMemoryCache();
        services.Configure<ReviewCacheOptions>(
            configuration.GetSection(ReviewCacheOptions.SectionName));
        services.AddSingleton<IReviewStore, InMemoryReviewStore>();
        services.Configure<GitHubOptions>(configuration.GetSection(GitHubOptions.SectionName));
        services.AddHttpClient("GitHub", (provider, client) =>
        {
            var options = provider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubOptions>>()
                .Value;

            client.BaseAddress = new Uri(options.ApiBaseUrl);
            client.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

            if (!string.IsNullOrWhiteSpace(options.Token))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.Token);
            }
        });
        services.AddSingleton<IGitHubPullRequestClient>(p => new GitHubPullRequestClient(p.GetRequiredService<IHttpClientFactory>().CreateClient("GitHub")));
        services.AddSingleton<IPullRequestContextProvider>(p => new GitHubContextProvider(p.GetRequiredService<IHttpClientFactory>().CreateClient("GitHub"), p.GetRequiredService<IGitHubPullRequestClient>()));
        services.AddScoped<AiRequestCredentials>();
        services.Configure<AiOptions>(configuration.GetSection("AI"));
        services.Configure<RunnerOptions>(configuration.GetSection("ExecutionRunner"));
        services.AddSingleton(p => p.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>().Value.Limits);
        services.AddHttpClient("AI", client => client.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<IAiModelClient>(p => new OpenAiModelClient(p.GetRequiredService<IHttpClientFactory>().CreateClient("AI"), p.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>(), p.GetRequiredService<AiRequestCredentials>()));
        services.AddScoped<IAiModelClient>(p => new GeminiModelClient(p.GetRequiredService<IHttpClientFactory>().CreateClient("AI"), p.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>(), p.GetRequiredService<AiRequestCredentials>()));
        services.AddScoped<IAiModelClient>(p => new DeepSeekModelClient(p.GetRequiredService<IHttpClientFactory>().CreateClient("AI"), p.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>(), p.GetRequiredService<AiRequestCredentials>()));
        services.AddScoped<IAiRegistry, AiRegistry>();
        services.AddSingleton<ISkillCatalog, FileSystemSkillCatalog>();
        services.AddSingleton<IModelInputFormatter, ToonInputFormatter>();
        services.AddHttpClient("Runner", client => client.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IExecutionRunner>(p => new HttpExecutionRunner(p.GetRequiredService<IHttpClientFactory>().CreateClient("Runner"), p.GetRequiredService<Microsoft.Extensions.Options.IOptions<RunnerOptions>>(), p.GetRequiredService<IPullRequestContextProvider>()));
        services.AddScoped<ReviewPipeline>();
        services.AddSingleton<IRepositoryAnalyzer, RepositoryAnalyzer>();
        return services;
    }
}
