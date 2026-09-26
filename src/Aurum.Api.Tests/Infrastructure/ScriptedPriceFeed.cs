using Aurum.App.Infrastructure.Pricing.Sources;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// An <see cref="IPriceFeed"/> that answers from a script and signals every call.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WaitForCallAsync"/> is not convenience, it is the only way these tests are not flaky.
/// <c>FakeTimeProvider</c>, <c>PeriodicTimer</c> and <c>BackgroundService</c> interleave
/// nondeterministically: advancing the clock schedules the next tick, it does not run the poll, so
/// a test that advances and then asserts is asserting against whatever the thread pool happened to
/// finish. Every test here awaits the call it expects and then asserts that no further call
/// arrives within a timeout.
/// </para>
/// <para>
/// CLAUDE.md's rule applies to all of them: a concurrency test that passes on the first try should
/// be checked for whether it is actually racing before it is believed.
/// </para>
/// </remarks>
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

            // Complete the waiter for THIS call if a test is already parked on it, or park a
            // completed one for a test that gets here afterwards. Either order works, which is the
            // property that makes the wait race-free rather than merely usually-fine.
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
