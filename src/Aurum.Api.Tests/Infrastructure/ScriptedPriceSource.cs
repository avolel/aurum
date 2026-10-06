using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// An <see cref="IPriceSource"/> that does what a test tells it to and counts its calls, so a test
/// can prove a source was skipped rather than called and ignored (which still spends a lease).
/// </summary>
internal sealed class ScriptedPriceSource(string code, Func<PriceQuote> behaviour)
    : IPriceSource
{
    private int _callCount;

    public string Code => code;

    public int CallCount => Volatile.Read(ref _callCount);

    public Task<PriceQuote> GetLatestQuoteAsync(string symbol, CancellationToken ct)
    {
        Interlocked.Increment(ref _callCount);

        try
        {
            return Task.FromResult(behaviour());
        }
        catch (Exception ex)
        {
            // A faulted task, not a synchronous throw: that is the shape a real async source fails in.
            return Task.FromException<PriceQuote>(ex);
        }
    }

    public static ScriptedPriceSource Succeeding(
        string code, decimal mid = 4_000m, DateTimeOffset? at = null)
    {
        var observedAt = at ?? DateTimeOffset.UnixEpoch;

        return new ScriptedPriceSource(code, () => PriceQuote.Normalize(
            SupportedSymbol.Gold, observedAt, observedAt, null, null, mid, code));
    }

    public static ScriptedPriceSource Throwing(string code, Func<Exception> error) =>
        new(code, () => throw error());

    public static ScriptedPriceSource Faulting(string code, string message = "boom") =>
        Throwing(code, () => new PriceSourceException(code, message));

    public static ScriptedPriceSource OutOfQuota(string code, DateTimeOffset resetsAt) =>
        Throwing(code, () => new QuotaExhaustedException(code, resetsAt));
}
