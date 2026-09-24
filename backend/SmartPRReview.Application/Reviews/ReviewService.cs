using SmartPRReview.Application.Abstractions;
using SmartPRReview.Application.AI;
using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Application.Reviews;

public sealed class ReviewService(IReviewStore store, IAiRegistry registry, ReviewPipeline pipeline, TimeProvider timeProvider, AiRequestCredentials credentials)
{
    public ResolvedAi Validate(AiSelection? selection, string? apiKey = null)
    {
        credentials.Set(apiKey);
        return registry.Resolve(selection);
    }
    public async Task<Review> CreateAsync(CreateReviewCommand command, CancellationToken cancellationToken, ProgressSink? progress = null)
    {
        var selection = Validate(command.Ai, command.AiApiKey);
        var review = Review.Create(Guid.NewGuid(), command.Repository, timeProvider.GetUtcNow());
        await store.SetAsync(review, cancellationToken);
        if (progress is not null) await progress(new("started", review.Id, "starting", "Iniciando revisión."), cancellationToken);
        return await pipeline.RunAsync(review, selection, command.GitHubToken, progress, cancellationToken);
    }
    public Task<Review?> GetAsync(Guid id, CancellationToken cancellationToken) => store.GetAsync(id, cancellationToken);
}
