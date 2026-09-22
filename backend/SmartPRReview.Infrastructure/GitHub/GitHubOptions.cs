namespace SmartPRReview.Infrastructure.GitHub;

public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";
    public string ApiBaseUrl { get; init; } = "https://api.github.com/";
    public string? Token { get; init; }
    public string UserAgent { get; init; } = "SmartPRReview";
}

