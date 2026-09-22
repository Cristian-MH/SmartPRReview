namespace SmartPRReview.Infrastructure.GitHub;

internal sealed record GitHubRepositoryLocation(string Owner, string Name)
{
    public static GitHubRepositoryLocation Parse(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            throw new ArgumentException("Repository location is required.", nameof(location));
        }

        var normalized = location.Trim().TrimEnd('/');
        string path;

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("GitHub repository location must use github.com.", nameof(location));
            }

            path = uri.AbsolutePath;
        }
        else if (normalized.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            path = normalized["git@github.com:".Length..];
        }
        else
        {
            throw new ArgumentException(
                "GitHub repository location must be an HTTPS or SSH GitHub URL.",
                nameof(location));
        }

        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            throw new ArgumentException(
                "GitHub repository location must identify an owner and repository.",
                nameof(location));
        }

        var repositoryName = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];

        if (string.IsNullOrWhiteSpace(segments[0]) || string.IsNullOrWhiteSpace(repositoryName))
        {
            throw new ArgumentException("GitHub owner and repository cannot be empty.", nameof(location));
        }

        return new GitHubRepositoryLocation(segments[0], repositoryName);
    }
}

