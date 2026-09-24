namespace SmartPRReview.Domain.Reviews;

public sealed record RunnerCapabilities(string[] AllowedRepositories, string[] ProfileRepositories);
public sealed record ExecutionRequest(string BaseRepository, string HeadRepository, string HeadSha, byte[] Archive);
