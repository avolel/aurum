using Aurum.App.Infrastructure.Pricing.Sources;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// An <see cref="IPriceFeed"/> that answers from a script and signals every call. Advancing a fake
/// clock only schedules a poll, so tests await <see cref="WaitForCallAsync"/> instead of asserting straight away.
/// </summary>
internal sealed class ScriptedPriceFeed(Func<int, PriceFeedResult> behaviour) : IPriceFeed
{
    private readonly List<TaskCompletionSource> _calls = [];
    private readonly Lock _gate = new();
    private int _callCount;

    public int CallCount
    {
        get { lock (_gate) { return _callCount; } }
    }

    public Task<PriceFeedResult> GetLatestQuoteAsync(string symbol, CancellationToken ct)
    {
        int ordinal;

        lock (_gate)
        {
            ordinal = _callCount++;

            // Works whether the test starts waiting before or after this call arrives.
            while (_calls.Count <= ordinal)
            {
                _calls.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            _calls[ordinal].TrySetResult();
        }

        try
        {
            return Task.FromResult(behaviour(ordinal));
        }
        catch (Exception ex)
        {
            return Task.FromException<PriceFeedResult>(ex);
        }
    }

    /// <summary>Waits until the feed has been called <paramref name="count"/> times.</summary>
    public Task WaitForCallAsync(int count)
    {
        lock (_gate)
        {
            while (_calls.Count < count)
            {
                _calls.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            return _calls[count - 1].Task;
        }
    }
}
