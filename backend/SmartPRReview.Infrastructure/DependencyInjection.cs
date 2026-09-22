using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartPRReview.Application.Abstractions;
using SmartPRReview.Infrastructure.Analysis;
using SmartPRReview.Infrastructure.Caching;
using SmartPRReview.Infrastructure.GitHub;
using SmartPRReview.Infrastructure.Queue;

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
        services.AddSingleton<IReviewQueue, InMemoryReviewQueue>();
        services.Configure<GitHubOptions>(configuration.GetSection(GitHubOptions.SectionName));
        services.AddHttpClient<IGitHubPullRequestClient, GitHubPullRequestClient>((provider, client) =>
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
        services.AddSingleton<IRepositoryAnalyzer, RepositoryAnalyzer>();
        return services;
    }
}
