using SmartPRReview.Application.Abstractions;
using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Application.Reviews;

public sealed class ReviewService(IReviewStore store, IReviewQueue queue, TimeProvider timeProvider)
{
    public async Task<Review> CreateAsync(
        CreateReviewCommand command,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var review = Review.Create(Guid.NewGuid(), command.Repository, now);

        await store.SetAsync(review, cancellationToken);
        await queue.EnqueueAsync(review.Id, cancellationToken);

        return review;
    }

    public Task<Review?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        store.GetAsync(id, cancellationToken);
}

