using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Application.Reviews;

public sealed record CreateReviewCommand(RepositoryReference Repository);

