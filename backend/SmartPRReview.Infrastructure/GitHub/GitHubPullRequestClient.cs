using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SmartPRReview.Application.Abstractions;
using SmartPRReview.Application.Reviews;
using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Infrastructure.GitHub;

public sealed class GitHubPullRequestClient(HttpClient httpClient) : IGitHubPullRequestClient
{
    private const int PageSize = 100;

    public async Task<PullRequestSnapshot> GetAsync(
        string repositoryLocation,
        int pullRequestNumber,
        CancellationToken cancellationToken)
    {
        GitHubRepositoryLocation repository;
        try
        {
            repository = GitHubRepositoryLocation.Parse(repositoryLocation);
        }
        catch (ArgumentException exception)
        {
            throw new ReviewProcessingException(exception.Message, exception);
        }
        var prefix = $"repos/{Uri.EscapeDataString(repository.Owner)}/" +
                     $"{Uri.EscapeDataString(repository.Name)}/pulls/{pullRequestNumber}";

        using var response = await httpClient.GetAsync(prefix, cancellationToken);
        await EnsureSuccessAsync(response, repository, pullRequestNumber, cancellationToken);

        var pullRequest = await response.Content.ReadFromJsonAsync<GitHubPullRequestResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty pull request response.");

        var files = await GetFilesAsync(prefix, cancellationToken);

        return new PullRequestSnapshot(
            pullRequest.Number,
            pullRequest.Title,
            pullRequest.Body,
            pullRequest.State,
            pullRequest.Merged,
            pullRequest.User.Login,
            pullRequest.HtmlUrl,
            pullRequest.Base.Reference,
            pullRequest.Head.Reference,
            pullRequest.Base.Sha,
            pullRequest.Head.Sha,
            pullRequest.Commits,
            pullRequest.Additions,
            pullRequest.Deletions,
            pullRequest.ChangedFiles,
            files);
    }

    private async Task<IReadOnlyCollection<ChangedFileSnapshot>> GetFilesAsync(
        string prefix,
        CancellationToken cancellationToken)
    {
        var results = new List<ChangedFileSnapshot>();

        for (var page = 1; ; page++)
        {
            var files = await httpClient.GetFromJsonAsync<List<GitHubFileResponse>>(
                $"{prefix}/files?per_page={PageSize}&page={page}",
                cancellationToken) ?? [];

            results.AddRange(files.Select(file => new ChangedFileSnapshot(
                file.Filename,
                file.Status,
                file.Additions,
                file.Deletions,
                file.Changes,
                file.PreviousFilename,
                file.Patch)));

            if (files.Count < PageSize)
            {
                return results;
            }
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        GitHubRepositoryLocation repository,
        int pullRequestNumber,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var details = await response.Content.ReadAsStringAsync(cancellationToken);
        var target = $"{repository.Owner}/{repository.Name}#{pullRequestNumber}";

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new ReviewProcessingException(
                "GitHub rejected the configured token. Verify GitHub__Token."),
            HttpStatusCode.Forbidden => new ReviewProcessingException(
                "GitHub denied access or the API rate limit was exceeded."),
            HttpStatusCode.NotFound => new ReviewProcessingException(
                $"GitHub pull request '{target}' was not found or the token cannot access it."),
            _ => new HttpRequestException(
                $"GitHub returned {(int)response.StatusCode} for '{target}': {details}",
                null,
                response.StatusCode)
        };
    }

    private sealed record GitHubPullRequestResponse(
        int Number,
        string Title,
        string? Body,
        string State,
        bool Merged,
        GitHubUserResponse User,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        GitHubBranchResponse Base,
        GitHubBranchResponse Head,
        int Commits,
        int Additions,
        int Deletions,
        [property: JsonPropertyName("changed_files")] int ChangedFiles);

    private sealed record GitHubUserResponse(string Login);

    private sealed record GitHubBranchResponse(
        [property: JsonPropertyName("ref")] string Reference,
        string Sha);

    private sealed record GitHubFileResponse(
        string Filename,
        string Status,
        int Additions,
        int Deletions,
        int Changes,
        [property: JsonPropertyName("previous_filename")] string? PreviousFilename,
        string? Patch);
}
