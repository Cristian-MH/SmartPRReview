using System.ComponentModel.DataAnnotations;

namespace SmartPRReview.Api.Contracts;

public sealed record ListPullRequestsRequest(
    [Required] string Location,
    [Required, RegularExpression("^(all|open|closed)$")] string State = "all",
    string? GitHubToken = null);
