using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SmartPRReview.Application.Abstractions;
using SmartPRReview.Application.Reviews;
using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Infrastructure.GitHub;

public sealed class GitHubPullRequestClient(HttpClient httpClient) : IGitHubPullRequestClient
{
    private const int PageSize = 100;

    public async Task<IReadOnlyCollection<PullRequestSummary>> ListAsync(
        string repositoryLocation,
        string state,
        CancellationToken cancellationToken,
        string? gitHubToken = null)
    {
        var repository = GitHubRepositoryLocation.Parse(repositoryLocation);
        if (state is not ("all" or "open" or "closed"))
        {
            throw new ArgumentException("State must be all, open, or closed.", nameof(state));
        }

        var prefix = $"repos/{Uri.EscapeDataString(repository.Owner)}/" +
                     $"{Uri.EscapeDataString(repository.Name)}/pulls";
        var results = new List<PullRequestSummary>();
        for (var page = 1; ; page++)
        {
            using var request = CreateRequest(
                $"{prefix}?state={state}&sort=created&direction=desc&per_page={PageSize}&page={page}", gitHubToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            EnsureSuccess(response, repository, null);
            var items = await response.Content.ReadFromJsonAsync<List<GitHubPullRequestListItem>>(
                cancellationToken: cancellationToken)
                ?? throw new HttpRequestException("GitHub returned an empty list response.");
            results.AddRange(items.Select(item => new PullRequestSummary(
                item.Number, item.Title, item.Body, item.State, item.Draft,
                item.User.Login, item.HtmlUrl, item.Base.Reference, item.Head.Reference,
                item.CreatedAt, item.UpdatedAt, item.ClosedAt, item.MergedAt)));
            if (items.Count < PageSize)
            {
                return results;
            }
        }
    }

    public async Task<PullRequestSnapshot> GetAsync(
        string repositoryLocation,
        int pullRequestNumber,
        CancellationToken cancellationToken,
        string? gitHubToken = null)
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

        using var request = CreateRequest(prefix, gitHubToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        EnsureSuccess(response, repository, pullRequestNumber);

        var pullRequest = await response.Content.ReadFromJsonAsync<GitHubPullRequestResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty pull request response.");

        var files = await GetFilesAsync(prefix, repository, pullRequestNumber, gitHubToken, cancellationToken);

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
            files)
        {
            BaseRepository = pullRequest.Base.Repo?.FullName,
            HeadRepository = pullRequest.Head.Repo?.FullName
        };
    }

    private async Task<IReadOnlyCollection<ChangedFileSnapshot>> GetFilesAsync(
        string prefix,
        GitHubRepositoryLocation repository,
        int pullRequestNumber,
        string? gitHubToken,
        CancellationToken cancellationToken)
    {
        var results = new List<ChangedFileSnapshot>();

        for (var page = 1; ; page++)
        {
            using var request = CreateRequest($"{prefix}/files?per_page={PageSize}&page={page}", gitHubToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            EnsureSuccess(response, repository, pullRequestNumber);
            var files = await response.Content.ReadFromJsonAsync<List<GitHubFileResponse>>(
                cancellationToken: cancellationToken) ?? [];

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

    private static HttpRequestMessage CreateRequest(string path, string? gitHubToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrWhiteSpace(gitHubToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gitHubToken.Trim());
        }

        return request;
    }

    private static void EnsureSuccess(
        HttpResponseMessage response,
        GitHubRepositoryLocation repository,
        int? pullRequestNumber)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var target = pullRequestNumber.HasValue
            ? $"pull request '{repository.Owner}/{repository.Name}#{pullRequestNumber}'"
            : $"repository '{repository.Owner}/{repository.Name}'";

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new ReviewProcessingException(
                "GitHub rejected the token. Verify the request's gitHubToken or the configured GitHub__Token."),
            HttpStatusCode.Forbidden => new ReviewProcessingException(
                "GitHub denied access or the API rate limit was exceeded."),
            HttpStatusCode.NotFound => new ReviewProcessingException(
                $"GitHub {target} was not found or the token cannot access it."),
            _ => new HttpRequestException(
                $"GitHub returned {(int)response.StatusCode} for '{target}'.",
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

    private sealed record GitHubPullRequestListItem(
        int Number,
        string Title,
        string? Body,
        string State,
        bool Draft,
        GitHubUserResponse User,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        GitHubBranchResponse Base,
        GitHubBranchResponse Head,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
        [property: JsonPropertyName("closed_at")] DateTimeOffset? ClosedAt,
        [property: JsonPropertyName("merged_at")] DateTimeOffset? MergedAt);

    private sealed record GitHubBranchResponse(
        [property: JsonPropertyName("ref")] string Reference,
        string Sha,
        GitHubRepoResponse? Repo = null);

    private sealed record GitHubRepoResponse([property: JsonPropertyName("full_name")] string FullName);

    private sealed record GitHubFileResponse(
        string Filename,
        string Status,
        int Additions,
        int Deletions,
        int Changes,
        [property: JsonPropertyName("previous_filename")] string? PreviousFilename,
        string? Patch);
}
