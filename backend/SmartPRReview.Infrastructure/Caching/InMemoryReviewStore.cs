using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SmartPRReview.Application.Abstractions;
using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Infrastructure.Caching;

public sealed class InMemoryReviewStore(
    IMemoryCache cache,
    IOptions<ReviewCacheOptions> options) : IReviewStore
{
    private static string Key(Guid reviewId) => $"review:{reviewId:N}";

    public Task<Review?> GetAsync(Guid reviewId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        cache.TryGetValue(Key(reviewId), out Review? review);
        return Task.FromResult(review);
    }

    public Task SetAsync(Review review, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        cache.Set(Key(review.Id), review, options.Value.ReviewLifetime);
        return Task.CompletedTask;
    }
}

public sealed class ReviewCacheOptions
{
    public const string SectionName = "ReviewCache";
    public TimeSpan ReviewLifetime { get; init; } = TimeSpan.FromHours(24);
}

