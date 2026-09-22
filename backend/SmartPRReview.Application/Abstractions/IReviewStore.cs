using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Application.Abstractions;

public interface IReviewStore
{
    Task<Review?> GetAsync(Guid reviewId, CancellationToken cancellationToken);
    Task SetAsync(Review review, CancellationToken cancellationToken);
}

