namespace SmartPRReview.Application.Abstractions;

public interface IReviewQueue
{
    ValueTask EnqueueAsync(Guid reviewId, CancellationToken cancellationToken);
    ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken);
}

