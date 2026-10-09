using System.Collections.Concurrent;
using Aurum.App.Infrastructure.Pricing.Realtime;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// Records every publish and runs an optional step inside it, to observe state at that moment or
/// to throw.
/// </summary>
public sealed class RecordingBroadcaster(Func<string, Task>? onPublish = null) : IPriceBroadcaster
{
    private readonly ConcurrentQueue<string> _published = new();

    // RunContinuationsAsynchronously: the test must not resume on the poller's thread.
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyCollection<string> Published => _published;

    /// <summary>Completes once the first publish, including its step, has finished.</summary>
    public Task FirstPublish => _first.Task;

    public async Task PublishAsync(string symbol, CancellationToken ct)
    {
        _published.Enqueue(symbol);
        try
        {
            if (onPublish is not null)
            {
                await onPublish(symbol);
            }
        }
        finally
        {
            _first.TrySetResult();
        }
    }
}
