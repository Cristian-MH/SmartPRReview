using SmartPRReview.Application.Abstractions;
using SmartPRReview.Application.Reviews;

namespace SmartPRReview.Api.Workers;

public sealed class ReviewWorker(
    IReviewQueue queue,
    IReviewStore store,
    IRepositoryAnalyzer analyzer,
    TimeProvider timeProvider,
    ILogger<ReviewWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var reviewId = await queue.DequeueAsync(stoppingToken);
            var review = await store.GetAsync(reviewId, stoppingToken);

            if (review is null)
            {
                logger.LogWarning("Review {ReviewId} expired before processing", reviewId);
                continue;
            }

            try
            {
                review = review.Start(timeProvider.GetUtcNow());
                await store.SetAsync(review, stoppingToken);

                var result = await analyzer.AnalyzeAsync(review.Repository, stoppingToken);
                review = review.Complete(
                    result.Summary,
                    result.PullRequest,
                    result.Findings,
                    timeProvider.GetUtcNow());
                await store.SetAsync(review, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ReviewProcessingException exception)
            {
                logger.LogWarning(exception, "Review {ReviewId} could not be processed", reviewId);
                review = review.Fail(exception.Message, timeProvider.GetUtcNow());
                await store.SetAsync(review, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Review {ReviewId} failed", reviewId);
                review = review.Fail("The review could not be completed.", timeProvider.GetUtcNow());
                await store.SetAsync(review, CancellationToken.None);
            }
        }
    }
}
