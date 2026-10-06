using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// An <see cref="IPriceSource"/> that does exactly what a test tells it to, and counts its calls.
/// </summary>
/// <remarks>
/// <see cref="CallCount"/> is the point. Asserting that a quote came from the second source proves
/// only that the second source was reached; it does not prove the first was skipped rather than
/// called and ignored, and "called but skipped" is the failure mode an open circuit is supposed to
/// prevent — it still spends a lease.
/// </remarks>
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
            // Faulted task rather than a synchronous throw: a real source is async, and an
            // exception thrown before the first await surfaces at a different point in the caller's
            // try block. The chain must handle the shape it will actually meet.
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
