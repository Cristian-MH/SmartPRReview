using System.Threading.Channels;
using SmartPRReview.Application.Abstractions;

namespace SmartPRReview.Infrastructure.Queue;

public sealed class InMemoryReviewQueue : IReviewQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    public ValueTask EnqueueAsync(Guid reviewId, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(reviewId, cancellationToken);

    public ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}

