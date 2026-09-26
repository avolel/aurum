using Aurum.App.SharedKernel.Constants;
using Aurum.App.Infrastructure.Data.Entities.Pricing;

namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <summary>
/// The canonical tick shape from FR-1.2, before persistence.
/// </summary>
/// <param name="Symbol">e.g. <c>XAUUSD</c>.</param>
/// <param name="ObservedAt">Provider-reported validity time, in UTC.</param>
/// <param name="ReceivedAt">When we read the response. Drives the staleness we show the user.</param>
/// <param name="Bid">Null where the provider does not quote two-sided.</param>
/// <param name="Ask">Null where the provider does not quote two-sided.</param>
/// <param name="Mid">Always populated — see <see cref="Normalize"/>.</param>
/// <param name="SourceCode">Which <see cref="IPriceSource"/> produced this.</param>
public sealed record PriceQuote(
    string Symbol,
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt,
    decimal? Bid,
    decimal? Ask,
    decimal Mid,
    string SourceCode)
{
    /// <summary>
    /// Per-symbol plausibility band for a mid, in the quote currency per troy ounce.
    /// </summary>
    /// <remarks>
    /// This exists for one failure mode that has no other detector: a provider that quotes the
    /// inverse — MetalpriceAPI's <c>rates.XAUUSD</c> is ounces per USD (~0.00023) alongside the
    /// <c>rates.USD</c> we want (~4,349). <c>PriceTick.Mid</c> is <c>numeric(18,4)</c>, so an
    /// inverted rate rounds to <c>0.0002</c> and persists without an exception; the delta engine
    /// then reads a near-total crash off a healthy feed. Wide on purpose — it is an assertion
    /// about units, not a market-movement guard, and a band narrow enough to be interesting would
    /// take the feed down on a real spike.
    /// </remarks>
    private static readonly Dictionary<string, (decimal Min, decimal Max)> PlausibleMid =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [SupportedSymbol.Gold] = (100m, 50_000m),
            [SupportedSymbol.Silver] = (1m, 5_000m),
        };

    /// <summary>
    /// Builds a quote, deriving the mid when the provider does not give one directly.
    /// Prefers the provider's own mid: for some sources it is the traded last price rather
    /// than the midpoint, and substituting our own would silently change what the chart means.
    /// </summary>
    public static PriceQuote Normalize(
        string symbol,
        DateTimeOffset observedAt,
        DateTimeOffset receivedAt,
        decimal? bid,
        decimal? ask,
        decimal? providerMid,
        string sourceCode)
    {
        var mid = providerMid
            ?? (bid.HasValue && ask.HasValue ? (bid.Value + ask.Value) / 2m : (decimal?)null)
            ?? bid
            ?? ask
            ?? throw new PriceSourceException(sourceCode, "Response contained no usable price (no mid, bid or ask).");

        // An unknown symbol is not banded rather than rejected: the symbol gate belongs to each
        // source, and inventing a band for a metal we have no reference price for would fail the
        // chain over on a source that is working.
        if (PlausibleMid.TryGetValue(symbol, out var band) && (mid < band.Min || mid > band.Max))
        {
            throw new PriceSourceException(
                sourceCode,
                $"Mid {mid} for {symbol} is outside the plausible band {band.Min}–{band.Max} — likely an inverted or wrongly scaled rate.");
        }

        return new PriceQuote(symbol, observedAt.ToUniversalTime(), receivedAt.ToUniversalTime(), bid, ask, mid, sourceCode);
    }

    public PriceTick ToTick() => new()
    {
        Symbol = Symbol,
        ObservedAt = ObservedAt,
        ReceivedAt = ReceivedAt,
        Bid = Bid,
        Ask = Ask,
        Mid = Mid,
        SourceCode = SourceCode,
    };
}
